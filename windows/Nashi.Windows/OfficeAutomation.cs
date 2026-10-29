using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Nashi.Windows;

/// Converts with the user's installed Microsoft Office when it is present:
/// Word/PowerPoint/Excel render their own formats far faster and more
/// faithfully than LibreOffice. Office runs out of process and invisible,
/// macros are force-disabled, files open read-only and are never added to
/// recent files. Nashi only quits an Office instance it started itself and
/// only when it is still invisible with no documents of the user's in it.
/// Any failure or timeout returns false so the caller can use LibreOffice.
internal sealed class OfficeAutomation:IDisposable
{
    internal enum Job{WordPdf,PowerPointPdf,ExcelPdf,WordDocx}
    private sealed record Work(Job Job,string Input,string Output,TaskCompletionSource Done,bool StartOnly=false);
    private sealed class Instance(dynamic application,int? process,bool started){public dynamic Application=application;public int? Process=process;public bool Started=started;public bool Broken;}
    private readonly BlockingCollection<Work> queue=new();private readonly Dictionary<string,Instance> instances=[];private readonly Thread thread;private volatile bool unusable;
    // A dummy password stops Office from showing a password prompt for
    // protected files; those then fail fast and fall back.
    private const string NoPassword="\u0001nashi-no-password";

    private OfficeAutomation()
    {
        thread=new Thread(Loop){IsBackground=true,Name="Nashi Office automation"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }

    // ---- shared, warm instance ----
    // Starting Word or PowerPoint costs seconds and quitting it costs more, so
    // conversions share one automation thread whose hidden Office instances stay
    // open for a few minutes after the last job. A job therefore never waits for
    // Office to quit, and back-to-back conversions skip the cold start.
    internal static TimeSpan KeepWarm{get;set;}=TimeSpan.FromMinutes(3);
    private static readonly object sharing=new();private static OfficeAutomation? shared;
    private int users;private Timer? idle;
    /// The shared instance; pair every call with Release().
    internal static OfficeAutomation Acquire()
    {
        lock(sharing)
        {
            if(shared is null||shared.unusable)shared=new();
            shared.users++;shared.idle?.Change(Timeout.Infinite,Timeout.Infinite);return shared;
        }
    }
    internal void Release()
    {
        var retire=false;
        lock(sharing)
        {
            if(--users>0)return;
            if(!ReferenceEquals(shared,this)||unusable){if(ReferenceEquals(shared,this))shared=null;retire=true;}
            else{idle??=new Timer(_=>Expire());idle.Change(KeepWarm,Timeout.InfiniteTimeSpan);}
        }
        if(retire)_ = Task.Run(Dispose);
    }
    private void Expire()
    {
        lock(sharing){if(users>0||!ReferenceEquals(shared,this))return;shared=null;}
        Dispose();
    }
    /// Quits the warm Office instances now (Nashi is exiting).
    internal static void Shutdown()
    {
        OfficeAutomation? current;lock(sharing){current=shared;shared=null;}
        current?.Dispose();
    }
    /// Starts the Office application a conversion of these files will need
    /// while the request is still being merged and prepared, so the job finds
    /// it running. Does nothing for other conversions.
    internal static void Prewarm(IEnumerable<string> inputs,string format)
    {
        foreach(var application in inputs.Select(p=>For(Nashi.Core.FeatureCatalog.Extension(p),format)).OfType<Job>().DistinctBy(Name).ToArray())
        {
            var automation=Acquire();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try{automation.queue.Add(new Work(application,"","",started,StartOnly:true));}catch(InvalidOperationException){started.TrySetResult();}
            // A start stuck on a hidden prompt still lets the instance expire later.
            _ = Task.WhenAny(started.Task,Task.Delay(TimeSpan.FromSeconds(90))).ContinueWith(_=>automation.Release(),TaskScheduler.Default);
        }
    }

    internal static Job? For(string extension,string format)=>(extension,format) switch
    {
        ("docx" or "doc" or "docm" or "dotx" or "rtf" or "odt" or "txt","pdf")=>Installed("Word")?Job.WordPdf:null,
        // Word starts in seconds (and stays warm); LibreOffice needs ~7 s warm, ~20-30 s on a fresh profile.
        ("doc" or "docm" or "dotx" or "rtf" or "odt" or "txt","docx")=>Installed("Word")?Job.WordDocx:null,
        ("pptx" or "ppt" or "pptm" or "ppsx" or "pps" or "odp","pdf")=>Installed("PowerPoint")?Job.PowerPointPdf:null,
        ("xlsx" or "xls" or "xlsm" or "ods","pdf")=>Installed("Excel")?Job.ExcelPdf:null,
        ("pdf","docx")=>Installed("Word")?Job.WordDocx:null,
        _=>null
    };
    private static readonly ConcurrentDictionary<string,bool> registered=new();
    // An application that hung (activation prompt, stuck add-in) is skipped for
    // a while so later conversions go straight to LibreOffice.
    private static readonly ConcurrentDictionary<string,DateTime> stalled=new();
    private static bool Installed(string application)=>Environment.GetEnvironmentVariable("NASHI_DISABLE_MSOFFICE")!="1"&&!(stalled.TryGetValue(application,out var at)&&DateTime.UtcNow-at<TimeSpan.FromMinutes(30))&&registered.GetOrAdd(application,name=>Type.GetTypeFromProgID(name+".Application",false) is not null);

    /// Returns false when Office could not produce the file; the caller then
    /// falls back to LibreOffice. Cancellation is honoured.
    internal async Task<bool> TryConvertAsync(Job job,string input,string output,CancellationToken token)
    {
        if(unusable)return false;
        // Encrypted OOXML packages are OLE containers; Office would ask for a password.
        if(job is not Job.WordDocx&&Path.GetExtension(input).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx" or ".docm" or ".pptm" or ".xlsm" or ".ppsx" or ".dotx"){var head=new byte[2];await using(var file=File.OpenRead(input))if(await file.ReadAsync(head,token)<2||head[0]!='P'||head[1]!='K')return false;}
        var work=new Work(job,Path.GetFullPath(input),Path.GetFullPath(output),new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try{queue.Add(work,token);}catch(InvalidOperationException){return false;}
        var pages=job==Job.WordDocx?PdfPages(input):0;var limit=job==Job.WordDocx?TimeSpan.FromSeconds(Math.Min(900,90+pages*12)):TimeSpan.FromSeconds(Math.Min(900,90+new FileInfo(input).Length/1_000_000*4));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(limit);
        try{await work.Done.Task.WaitAsync(timeout.Token);return File.Exists(work.Output)&&new FileInfo(work.Output).Length>0;}
        catch(OperationCanceledException)
        {
            // Office is stuck (a hidden dialog, a hung add-in) or the user cancelled.
            // Only an invisible instance Nashi started itself is stopped.
            Abandon(Name(job));if(token.IsCancellationRequested)throw;stalled[Name(job)]=DateTime.UtcNow;unusable=true;return false;
        }
        catch(Exception error)when(error is not OperationCanceledException){return false;}
    }

    private static int PdfPages(string input){try{using var pdf=UglyToad.PdfPig.PdfDocument.Open(input);return pdf.NumberOfPages;}catch(Exception){return 50;}}
    private static string Name(Job job)=>job switch{Job.PowerPointPdf=>"PowerPoint",Job.ExcelPdf=>"Excel",_=>"Word"};
    private static string ProcessName(string application)=>application switch{"PowerPoint"=>"POWERPNT","Excel"=>"EXCEL",_=>"WINWORD"};

    private void Abandon(string application)
    {
        Instance? instance;lock(instances)instances.TryGetValue(application,out instance);
        if(instance?.Process is not {} id||!instance.Started){unusable=true;return;}
        try{using var process=Process.GetProcessById(id);if(process.ProcessName.Equals(ProcessName(application),StringComparison.OrdinalIgnoreCase)&&!HasVisibleWindow(id)){instance.Broken=true;process.Kill();}else unusable=true;}
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){}
    }

    private void Loop()
    {
        foreach(var work in queue.GetConsumingEnumerable())
        {
            try{if(work.StartOnly)Start(Name(work.Job));else RunWarm(work);work.Done.TrySetResult();}
            catch(Exception error)
            {
                // A failed call may leave the instance unusable; start fresh next time.
                lock(instances)if(instances.TryGetValue(Name(work.Job),out var instance)&&(instance.Broken||Disconnected(error))){instances.Remove(Name(work.Job));}
                work.Done.TrySetException(error);
            }
        }
        lock(instances)foreach(var pair in instances)Quit(pair.Key,pair.Value);
        instances.Clear();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();GC.WaitForPendingFinalizers();
    }

    private Instance Start(string application)
    {
        lock(instances)if(instances.TryGetValue(application,out var existing)&&!existing.Broken)return existing;
        var name=ProcessName(application);var before=Process.GetProcessesByName(name).Select(p=>{using(p)return p.Id;}).ToHashSet();
        var type=Type.GetTypeFromProgID(application+".Application",true)!;dynamic app=Retry(()=>Activator.CreateInstance(type)!);
        // Word and Excel start a new instance for every automation client;
        // PowerPoint is shared, so it counts as ours only if none was running.
        int? id=null;var started=application!="PowerPoint"||before.Count==0;
        if(application=="Excel"){try{_=GetWindowThreadProcessId(new nint(Convert.ToInt64(Retry(()=>(object)app.Hwnd),System.Globalization.CultureInfo.InvariantCulture)),out var pid);id=before.Contains(pid)?null:pid;}catch(Exception error)when(error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){}}
        else{var fresh=Process.GetProcessesByName(name).Select(p=>{using(p)return p.Id;}).Where(p=>!before.Contains(p)).ToArray();if(fresh.Length==1&&started)id=fresh[0];}
        if(application=="Word"){Retry(()=>{app.Visible=false;app.DisplayAlerts=0;app.ScreenUpdating=false;app.AutomationSecurity=3;return 0;});}
        if(application=="Excel"){Retry(()=>{app.Visible=false;app.DisplayAlerts=false;app.ScreenUpdating=false;app.AskToUpdateLinks=false;app.AutomationSecurity=3;return 0;});}
        var instance=new Instance(app,id,started);lock(instances)instances[application]=instance;return instance;
    }

    private static bool Disconnected(Exception error)=>error is COMException{HResult:unchecked((int)0x800706BA) or unchecked((int)0x80010108) or unchecked((int)0x800706BE)} or InvalidComObjectException;
    /// A warm instance may have gone away since the last job (the user quit the
    /// PowerPoint Nashi started, Office crashed): retry once on a fresh one
    /// instead of falling back to the slower LibreOffice.
    private void RunWarm(Work work)
    {
        bool reused;lock(instances)reused=instances.TryGetValue(Name(work.Job),out var cached)&&!cached.Broken;
        try{Run(work);}
        catch(Exception error)when(reused&&Disconnected(error)){lock(instances)instances.Remove(Name(work.Job));Run(work);}
    }
    private void Run(Work work)
    {
        var application=Name(work.Job);var instance=Start(application);dynamic app=instance.Application;
        if(File.Exists(work.Output))File.Delete(work.Output);
        switch(work.Job)
        {
            case Job.WordPdf or Job.WordDocx:
            {
                // Plain text arrives normalized to UTF-8 without a BOM; 65001 = msoEncodingUTF8, so Word does not guess a code page.
                var text=Nashi.Core.FeatureCatalog.Extension(work.Input)=="txt";
                dynamic document=Retry(()=>text
                    ?app.Documents.Open(FileName:work.Input,ConfirmConversions:false,ReadOnly:true,AddToRecentFiles:false,PasswordDocument:NoPassword,WritePasswordDocument:NoPassword,Revert:false,Visible:false,OpenAndRepair:false,NoEncodingDialog:true,Encoding:65001)
                    :app.Documents.Open(FileName:work.Input,ConfirmConversions:false,ReadOnly:true,AddToRecentFiles:false,PasswordDocument:NoPassword,WritePasswordDocument:NoPassword,Revert:false,Visible:false,OpenAndRepair:false,NoEncodingDialog:true));
                try
                {
                    // 17 = wdExportFormatPDF; bookmarks from headings, tagged PDF,
                    // print-quality output, document properties kept.
                    if(work.Job==Job.WordPdf)Retry(()=>{document.ExportAsFixedFormat(OutputFileName:work.Output,ExportFormat:17,OpenAfterExport:false,OptimizeFor:0,Range:0,Item:0,IncludeDocProps:true,KeepIRM:true,CreateBookmarks:1,DocStructureTags:true,BitmapMissingFonts:true,UseISO19005_1:false);return 0;});
                    else Retry(()=>{document.SaveAs2(FileName:work.Output,FileFormat:16,AddToRecentFiles:false);return 0;});
                }
                finally{try{Retry(()=>{document.Close(SaveChanges:0);return 0;});}finally{Marshal.FinalReleaseComObject(document);}}
                break;
            }
            case Job.PowerPointPdf:
            {
                // msoTrue=-1, msoFalse=0. No window: nothing appears even when
                // this is the user's own running PowerPoint.
                int security=Retry(()=>(int)app.AutomationSecurity);Retry(()=>{app.AutomationSecurity=3;return 0;});
                try
                {
                    dynamic presentation=Retry(()=>app.Presentations.Open(work.Input,-1,0,0));
                    try{Retry(()=>{presentation.SaveAs(work.Output,32);return 0;});}
                    finally{try{Retry(()=>{presentation.Close();return 0;});}finally{Marshal.FinalReleaseComObject(presentation);}}
                }
                finally{try{Retry(()=>{app.AutomationSecurity=security;return 0;});}catch(COMException){}}
                break;
            }
            case Job.ExcelPdf:
            {
                dynamic workbook=Retry(()=>app.Workbooks.Open(Filename:work.Input,UpdateLinks:0,ReadOnly:true,Password:NoPassword,WriteResPassword:NoPassword,IgnoreReadOnlyRecommended:true,Notify:false,AddToMru:false));
                try{Retry(()=>{workbook.ExportAsFixedFormat(Type:0,Filename:work.Output,Quality:0,IncludeDocProperties:true,IgnorePrintAreas:false,OpenAfterPublish:false);return 0;});}
                finally{try{Retry(()=>{workbook.Close(SaveChanges:false);return 0;});}finally{Marshal.FinalReleaseComObject(workbook);}}
                break;
            }
        }
    }

    private static void Quit(string application,Instance instance)
    {
        try
        {
            if(instance.Broken)return;dynamic app=instance.Application;
            // Never quit an instance the user is using or that holds any document.
            var documents=application switch{"PowerPoint"=>(int)app.Presentations.Count,"Excel"=>(int)app.Workbooks.Count,_=>(int)app.Documents.Count};
            var visible=application=="PowerPoint"?(int)app.Visible!=0:(bool)app.Visible;
            if(instance.Started&&documents==0&&!visible){if(application=="Word")app.Quit(SaveChanges:0);else app.Quit();}
        }
        catch(Exception error)when(error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){}
        finally{try{Marshal.FinalReleaseComObject(instance.Application);}catch(ArgumentException){}}
    }

    // Office rejects calls while it is busy (RPC_E_CALL_REJECTED /
    // RPC_E_SERVERCALL_RETRYLATER); those are retried briefly.
    private static T Retry<T>(Func<T> call)
    {
        for(var attempt=0;;attempt++)
        {
            try{return call();}
            catch(COMException error)when(attempt<100&&error.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A)){Thread.Sleep(100);}
        }
    }
    private static bool HasVisibleWindow(int process)
    {
        var visible=false;EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out var owner);if(owner==process&&IsWindowVisible(window)){visible=true;return false;}return true;},0);return visible;
    }
    private delegate bool EnumWindowsProc(nint window,nint parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowsProc callback,nint parameter);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out int process);

    private int disposed;
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;idle?.Dispose();
        queue.CompleteAdding();
        // Give Office a moment to quit cleanly; a stuck call is abandoned.
        if(!thread.Join(TimeSpan.FromSeconds(unusable?0:20)))foreach(var application in new[]{"Word","PowerPoint","Excel"})Abandon(application);
        queue.Dispose();
    }
}

using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Nashi.Core;
namespace Nashi.Windows;

/// "Convert to FORMAT" without the main window: the Explorer menu, the
/// command line and wheel drops while Nashi sits in the tray. It uses the same
/// engines, saved export profiles (ToolEditor.AcceptDefaults), output location
/// rules, Library record and progress toast as the main window's wheel path.
/// Requests arriving within a short window are merged, so a multi-file
/// Explorer selection (one process per file in the classic menu) becomes one job.
internal sealed class HeadlessConversion
{
    private static string ToolsRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nashi","Tools");
    private readonly PreferencesStore preferences;private readonly LibraryStore library;
    private readonly Dictionary<string,List<string>> pending=new(StringComparer.Ordinal);private readonly Queue<(string Format,string[] Files)> jobs=new();
    private readonly DispatcherTimer debounce;private bool running;private CancellationTokenSource? active;private ToastWindow? finished;
    internal bool Busy=>running||jobs.Count>0||pending.Count>0;
    /// Raised on the UI thread when the queue drains, and again when the last
    /// "done" toast has faded (a one-shot process exits only then).
    internal event Action? Idle;
    /// Raised after outputs are recorded in Library.
    internal event Action? Recorded;
    internal HeadlessConversion(PreferencesStore preferences,LibraryStore library,TimeSpan? merge=null)
    {
        this.preferences=preferences;this.library=library;debounce=new(){Interval=merge??TimeSpan.FromMilliseconds(450)};debounce.Tick+=(_,_)=>{debounce.Stop();Flush();};
    }
    /// Command-line/Explorer requests are merged for a moment (the classic menu
    /// starts one process per selected file); a wheel drop already carries the
    /// whole selection and starts at once.
    internal void Enqueue(string format,IEnumerable<string> files,bool merge=true)
    {
        if(!pending.TryGetValue(format,out var list))pending[format]=list=[];
        var arrived=files.ToArray();list.AddRange(arrived);debounce.Stop();
        // Word/PowerPoint start during the merge window instead of after it.
        OfficeAutomation.Prewarm(arrived,format);
        if(merge)debounce.Start();else Flush();
    }
    internal void Cancel(){pending.Clear();jobs.Clear();active?.Cancel();}
    private void Flush()
    {
        var skipped=new List<string>();var skippedFormat="";
        foreach(var (format,files) in pending)
        {
            var plan=ConversionRoute.Plan(files,format);foreach(var job in plan.Jobs)jobs.Enqueue((format,job));
            if(plan.Skipped.Count>0){skipped.AddRange(plan.Skipped);skippedFormat=format;}
        }
        pending.Clear();
        if(skipped.Count>0)MessageBox.Show(L10n.Text($"{skipped.Count} files can’t become {skippedFormat.ToUpperInvariant()} and were skipped."),"Nashi",MessageBoxButton.OK,MessageBoxImage.Information);
        _ = PumpAsync();
    }
    private async Task PumpAsync()
    {
        if(running)return;running=true;
        try{while(jobs.TryDequeue(out var job))await RunAsync(job.Format,job.Files);}
        finally{running=false;if(!Busy)Idle?.Invoke();}
    }
    private async Task RunAsync(string format,string[] selected)
    {
        var tools=new Toolchain(Path.Combine(ToolsRoot,"v1"));var label="Convert to "+format.ToUpperInvariant();Func<string,IProgress<OperationProgress>,CancellationToken,Task<IReadOnlyList<string>>>? work;
        try{work=await PrepareAsync(selected,format,tools);}
        catch(Exception error){MessageBox.Show(L10n.Text(error.Message),L10n.Text("Couldn’t open this tool"),MessageBoxButton.OK,MessageBoxImage.Information);return;}
        if(work is null)return;
        string destination;var settings=preferences.Value;
        if(settings.OutputMode=="ask"){var folder=new OpenFolderDialog{Title="Save new files to…",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};if(folder.ShowDialog()!=true)return;destination=folder.FolderName;}
        else destination=settings.OutputMode=="folder"&&!string.IsNullOrWhiteSpace(settings.OutputFolder)?settings.OutputFolder:Path.GetDirectoryName(selected[0])!;
        // A previous job's "done" toast gives way to this job's progress.
        finished?.Close();
        using var cancellation=new CancellationTokenSource();active=cancellation;var toast=new ToastWindow(null,cancellation.Cancel);toast.Show();
        try
        {
            var outputs=await work(destination,new Progress<OperationProgress>(toast.Update),cancellation.Token);toast.Complete(outputs);
            // The next job (and the trim timer) does not wait for the toast to
            // fade; a one-shot process still exits only after it has closed.
            finished=toast;toast.Closed+=(_,_)=>{if(ReferenceEquals(finished,toast))finished=null;if(!Busy)Idle?.Invoke();};
            try{if(!ExplorerIntegration.Isolated){library.Record(label,outputs);Recorded?.Invoke();}}catch(Exception error)when(error is IOException or UnauthorizedAccessException){/* Outputs are saved; Library is best effort. */}
            if(settings.RevealFinished&&outputs.Count>0)MainWindow.Reveal(outputs[0]);
        }
        catch(OperationCanceledException){toast.Close();}
        catch(Exception error){toast.Close();MessageBox.Show(L10n.Text(error.Message),L10n.Text("Couldn’t finish"),MessageBoxButton.OK,MessageBoxImage.Information);}
        finally{active=null;}
    }
    /// Mirrors MainWindow.EditAsync / EditDocumentAsync for a requested format.
    private async Task<Func<string,IProgress<OperationProgress>,CancellationToken,Task<IReadOnlyList<string>>>?> PrepareAsync(string[] selected,string format,Toolchain tools)
    {
        var input=selected[0];var title="Convert to "+format.ToUpperInvariant();
        switch(ConversionRoute.Engine(selected,format))
        {
            case ConversionEngine.Image:
            {
                var editor=new ToolEditor(null,"convert",title,input,null,ImageEngine.Load(input),selected,FeatureCatalog.Formats(selected));
                try
                {
                    editor.ConfigureProfiles(preferences,format);editor.ConfigureMediaRuntime(tools);if(!editor.AcceptDefaults(format))return null;
                    var inputs=editor.OrderedInputs;var options=editor.ImageOptions with{EncoderPath=tools.Ready?tools.Executable("ffmpeg"):null};var preserve=editor.PreserveImageMetadata&&format is not ("pdf" or "bmp" or "gif");
                    var metadata=new MetadataService(ToolsRoot);
                    return async(destination,p,t)=>
                    {
                        using var tx=new ExportTransaction(destination);var exported=await OnStaAsync(()=>ImageEngine.Apply("convert",inputs,tx.StagingDirectory,options,format,t,p));
                        if(preserve){await metadata.InstallAsync(p,t);await metadata.CopyImageTagsAsync(exported.Select((output,n)=>(inputs[n],output)).ToArray(),t);}
                        return tx.Publish(exported,t);
                    };
                }
                finally{editor.Discard();}
            }
            case ConversionEngine.Media:
            {
                if(!tools.Ready){MessageBox.Show(L10n.Text("Choose Set up media tools once to enable media editing."),L10n.Text("Media setup"));return null;}
                var engine=new MediaEngine(tools.Executable("ffmpeg"),tools.Executable("ffprobe"));var info=await engine.InspectAsync(input);
                var editor=new ToolEditor(null,"convert",title,input,info,null,selected,FeatureCatalog.Formats(selected));
                try
                {
                    editor.ConfigureProfiles(preferences,format);editor.ConfigureMediaRuntime(tools);if(!editor.AcceptDefaults(format))return null;
                    var inputs=editor.OrderedInputs;var options=editor.MediaOptions;
                    return(destination,p,t)=>engine.ConvertAsync(inputs,format,destination,options,p,t);
                }
                finally{editor.Discard();}
            }
            case ConversionEngine.Document:
            {
                var editor=new ToolEditor(null,"convert","Convert",input,inputs:selected,outputFormats:FeatureCatalog.Formats(selected));
                try
                {
                    editor.ConfigureProfiles(preferences,format);if(!editor.AcceptDefaults(format))return null;var options=editor.DocumentOptions;
                    return(destination,p,t)=>new DocumentConversion(new OfficeRuntime(ToolsRoot),tools).ConvertAsync(selected,format,destination,options,p,t);
                }
                finally{editor.Discard();}
            }
            default:throw new ArgumentException($"{selected.Length} files can’t become {format.ToUpperInvariant()} and were skipped.");
        }
    }
    private static Task<IReadOnlyList<string>> OnStaAsync(Func<IReadOnlyList<string>> work)
    {
        var completion=new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(work());}catch(Exception error){completion.SetException(error);}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}

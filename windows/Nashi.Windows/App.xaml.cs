using System.Windows;
using Nashi.Core;
namespace Nashi.Windows;
public partial class App:Application
{
    private SingleInstance? instance;
    internal static bool IsSmokeTest{get;private set;}
    protected override void OnStartup(StartupEventArgs e)
    {
        if(Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY") is {} diagnostics)
        {
            Directory.CreateDirectory(diagnostics);
            AppDomain.CurrentDomain.UnhandledException+=(_,error)=>File.WriteAllText(Path.Combine(diagnostics,$"unhandled-{Environment.ProcessId}.txt"),error.ExceptionObject.ToString());
            Exit+=(_,exit)=>File.WriteAllText(Path.Combine(diagnostics,$"process-exit-{Environment.ProcessId}.json"),System.Text.Json.JsonSerializer.Serialize(new{mode=e.Args.FirstOrDefault(),code=exit.ApplicationExitCode,environmentCode=Environment.ExitCode}));
        }
        DispatcherUnhandledException+=(_,error)=>{var folder=Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY");if(folder is not null){Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"dispatcher-error.txt"),error.Exception.ToString());error.Handled=true;Shutdown(1);}};
        base.OnStartup(e);
        if(e.Args.Contains("--ui-audit"))
        {
            IsSmokeTest=true;ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try
            {
                var index=Array.IndexOf(e.Args,"--ui-audit");
                if(index+1>=e.Args.Length||e.Args[index+1].StartsWith("--"))throw new ArgumentException("Use --ui-audit OUTPUT [--scenes scene,...] [--appearance light|dark|both] [--language en|tr|both]");
                string[]? scenes=null,appearances=null,languages=null;
                for(var next=index+2;next<e.Args.Length;next+=2)
                {
                    if(next+1>=e.Args.Length)throw new ArgumentException("Missing value for "+e.Args[next]);
                    var value=e.Args[next+1];
                    switch(e.Args[next])
                    {
                        case "--scenes":scenes=value.Split(',');break;
                        case "--appearance":appearances=value=="both"?null:[value];break;
                        case "--language":languages=value=="both"?null:[value];break;
                        default:throw new ArgumentException("Unknown audit argument: "+e.Args[next]);
                    }
                }
                Shutdown(UIAudit.Run(e.Args[index+1],scenes,appearances,languages));
            }catch(Exception error){Console.Error.WriteLine(error.Message);Shutdown(64);}
            return;
        }
        if(e.Args.Contains("--window-management-fixture"))
        {
            var key=Array.IndexOf(e.Args,"--minimum-window-width");var minimum=key>=0&&key+1<e.Args.Length&&int.TryParse(e.Args[key+1],out var parsed)?Math.Clamp(parsed,160,1200):160;
            new Window{Title=minimum>620?"Nashi Constrained Layout Fixture":"Nashi Window Layout Fixture",Width=Math.Max(620,minimum),MinWidth=minimum,Height=420,Left=80,Top=90,WindowStartupLocation=WindowStartupLocation.Manual,
                Content=new System.Windows.Controls.TextBlock{Text="Nashi window arrangement test fixture",Margin=new Thickness(30),FontSize=22}}.Show();return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--apply-update"){try{Shutdown(UpdateInstaller.Apply(e.Args[1]));}catch(Exception error){UseSavedLanguage();MessageBox.Show(L10n.Text(error.Message),L10n.Text("Nashi update could not finish"));Shutdown(1);}return;}
        if(e.Args.Contains("--verify-update-state")){try{ShutdownMode=ShutdownMode.OnExplicitShutdown;SmokeTests.VerifyUpdateState();Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);Shutdown(1);}return;}
        if(e.Args.Contains("--smoke-test")){IsSmokeTest=true;try{NativeExceptionTrace.Install();SmokeTests.Run();Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);var directory=Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY");if(directory is not null){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());}Shutdown(1);}return;}
        if(e.Args.Length==1&&e.Args[0]=="--install-update"){ShutdownMode=ShutdownMode.OnExplicitShutdown;_ = InstallUpdateAsync();return;}
        // An elevated Nashi cannot receive drops from Explorer (UIPI): start again at the shell's level.
        if(!ExplorerIntegration.Isolated&&Elevation.IsElevated&&Elevation.ShellIsUnelevated()&&Elevation.RelaunchThroughShell(e.Args)){ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(0);return;}
        LaunchCommand command;
        try{command=LaunchCommand.Parse(e.Args);DeleteSelectionList(e.Args);}
        catch(ArgumentException error){UseSavedLanguage();MessageBox.Show(L10n.Text(error.Message),"Nashi",MessageBoxButton.OK,MessageBoxImage.Information);ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(64);return;}
        instance=new SingleInstance();Exit+=(_,_)=>instance.Dispose();ShutdownMode=ShutdownMode.OnExplicitShutdown;
        if(!instance.Primary){_ = ForwardAndExit(command);return;}
        // Light resident mode: tray, gesture and Explorer requests first; the
        // main window, Library and media engines only when they are needed.
        host=new ResidentHost(this);var resident=host;
        instance.Start(message=>Dispatcher.BeginInvoke(()=>resident.Handle(LaunchCommand.FromMessage(message))));
        resident.Handle(command);
    }
    private ResidentHost? host;
    /// NashiShell.exe passes very large Explorer selections as a temporary list.
    private static void DeleteSelectionList(string[] args)
    {
        var index=Array.FindIndex(args,a=>a.Equals("--files-from",StringComparison.OrdinalIgnoreCase));if(index<0||index+1>=args.Length)return;
        try{var list=Path.GetFullPath(args[index+1]);if(Path.GetFileName(list).StartsWith("Nashi-files-",StringComparison.Ordinal)&&string.Equals(Path.GetDirectoryName(list)+Path.DirectorySeparatorChar,Path.GetTempPath(),StringComparison.OrdinalIgnoreCase))File.Delete(list);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(int process);
    internal static void UseSavedLanguage(Func<string>? readLanguage=null)
    {
        L10n.Language="en";
        try{L10n.Language=readLanguage is null?new Nashi.Core.PreferencesStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nashi")).Value.Language:readLanguage();}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){/* Preserve the original error dialog in English. */}
    }
    private async Task ForwardAndExit(LaunchCommand command)
    {
        // Explorer gave this process the right to take the foreground; pass it
        // on so the resident instance can activate the tools wheel or window.
        AllowSetForegroundWindow(-1);
        try{foreach(var message in command.ToMessages())await instance!.Notify(message);Shutdown(0);}catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException){UseSavedLanguage();MessageBox.Show(L10n.Text("Nashi is already running but is still starting. Open it from the system tray or try again in a moment."),"Nashi");Shutdown(1);}
    }
    protected override void OnExit(ExitEventArgs e){host?.Dispose();OfficeAutomation.Shutdown();Feedback.Stop();PdfRasterDocument.Shutdown();base.OnExit(e);}
    private async Task InstallUpdateAsync()
    {
        try
        {
            var update=await Nashi.Core.PublicUpdates.CheckAsync(UpdateInstaller.Current);
            if(update is not null)await UpdateInstaller.StageAsync(update,new Progress<Nashi.Core.OperationProgress>(),CancellationToken.None);
            Shutdown(0);
        }
        catch(Exception error)
        {
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nashi");Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"update-error.txt"),error.ToString());Shutdown(1);
        }
    }
}

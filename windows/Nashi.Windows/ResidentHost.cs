using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Nashi.Core;
namespace Nashi.Windows;

/// The light, always-available part of Nashi. It owns the tray icon, the drag
/// gesture, window management, update checks and headless conversions, and
/// creates the main window (with its Library, thumbnails and media engines)
/// only when it is needed. When the window goes back to the tray it is released
/// and the working set is trimmed, so an idle Nashi stays in the tens of MB.
///
/// A "transient" host serves a single Explorer/command-line request without
/// tray or hooks and exits when that work is done, unless a normal launch
/// promotes it to the resident instance meanwhile.
internal sealed class ResidentHost:IDisposable
{
    [DllImport("kernel32.dll")]private static extern bool SetProcessWorkingSetSizeEx(IntPtr process,nint minimum,nint maximum,uint flags);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out GlobalDragController.NativePoint point);
    internal PreferencesStore Preferences{get;}
    internal LibraryStore Library{get;}
    private readonly Application app;private readonly HwndSource window;private readonly HeadlessConversion conversions;
    private readonly DispatcherTimer trim=new(){Interval=TimeSpan.FromSeconds(20)},releaseRetry=new(){Interval=TimeSpan.FromSeconds(2)},wheelMerge=new(){Interval=TimeSpan.FromMilliseconds(350)},updates=new(){Interval=TimeSpan.FromHours(1)},hookRefresh=new(){Interval=TimeSpan.FromMinutes(1)};
    private readonly List<string> wheelPaths=[];
    private TrayHost? tray;private GlobalDragController? drag;private WindowManagement? windowManagement;private MainWindow? main;private WheelWindow? toolsWheel;private WindowsUpdate? update;
    private bool desktop,quitting,checking;private int externalActions;
    internal bool DesktopStarted=>desktop;
    /// Closing the main window keeps Nashi alive in the tray (Settings › Keep running in the system tray).
    internal bool KeepsRunning=>desktop&&tray is not null&&Preferences.Value.KeepInTray;
    internal bool MainCreated=>main is not null;
    internal GlobalDragController? Drag=>drag;

    internal ResidentHost(Application app)
    {
        this.app=app;Library=new LibraryStore(ExplorerIntegration.IsolatedDataDirectory);Preferences=new(Library.DirectoryPath);ApplyPreferences();Theme.Apply(Library.Appearance);
        // A hidden top-level tool window (not message-only) so TaskbarCreated
        // still reaches the tray icon after an Explorer restart.
        window=new HwndSource(new HwndSourceParameters("Nashi")
        {
            WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x80|0x08000000,Width=0,Height=0,PositionX=-32000,PositionY=-32000,
        });
        conversions=new(Preferences,Library);conversions.Idle+=AfterWork;conversions.Recorded+=()=>main?.LibraryChanged();
        trim.Tick+=(_,_)=>{trim.Stop();Trim();};releaseRetry.Tick+=(_,_)=>TryRelease();
        wheelMerge.Tick+=(_,_)=>{wheelMerge.Stop();var paths=wheelPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();wheelPaths.Clear();ShowToolsWheel(paths);};
        updates.Tick+=async(_,_)=>await CheckForUpdatesAsync();hookRefresh.Tick+=(_,_)=>drag?.RefreshHook();
    }
    internal void ApplyPreferences(){var p=Preferences.Value;L10n.Language=p.Language;Feedback.Enabled=p.Feedback;WheelPresentation.Preferences=p;}
    /// Settings changed (from the Settings window or another source).
    internal void PreferencesChanged()
    {
        ApplyPreferences();drag?.UpdateShortcut();
        if(desktop)SyncRegistry();
    }
    private void SyncRegistry()
    {
        try{ExplorerIntegration.SyncLaunchAtLogin(Preferences.Value.LaunchAtLogin);ExplorerIntegration.SyncClassicMenu(Preferences.Value.ExplorerMenu,Preferences.Value.Language);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){/* Integration is optional; Nashi keeps working. */}
    }

    /// Tray icon, drag gesture, window management, update checks.
    internal void StartDesktop()
    {
        if(desktop)return;desktop=true;
        try{tray=new(window,()=>ShowMain(),TrayMenu);}catch(InvalidOperationException){tray=null;}
        drag=new(window,()=>main is null?!AnyModal():DesktopGeometry.IsInteractive(main),Preferences,OnGlobalDrop,CopiedLink,registerShortcut:!ExplorerIntegration.Isolated);
        // After a wheel, wait longer before trimming so quick repeat drags stay warm.
        drag.GestureEnded+=()=>TrimSoon(TimeSpan.FromMinutes(2));hookRefresh.Start();
        windowManagement=new WindowManagement(notice:message=>MessageBox.Show(L10n.Text(message),L10n.Text("Window arrangement"),MessageBoxButton.OK,MessageBoxImage.Information),isEnabled:()=>Library.WindowManagementEnabled);
        SyncRegistry();
        updates.Start();_ = Dispatcher.CurrentDispatcher.InvokeAsync(async()=>{await Task.Delay(TimeSpan.FromSeconds(20));await CheckForUpdatesAsync();});
        TrimSoon();
    }
    private static bool AnyModal()=>Application.Current.Windows.OfType<Window>().Any(w=>w.IsVisible&&!DesktopGeometry.IsInteractive(w));

    internal void Handle(LaunchCommand command)
    {
        switch(command.Kind)
        {
            case LaunchKind.Open:StartDesktop();ShowMain(command.Paths);break;
            case LaunchKind.Background:StartDesktop();break;
            case LaunchKind.Convert:if(command.Paths.Length>0)conversions.Enqueue(command.Format!,command.Paths,merge:!command.Whole);else AfterWork();break;
            case LaunchKind.Wheel:wheelPaths.AddRange(command.Paths);wheelMerge.Stop();wheelMerge.Start();break;
        }
    }

    // ---- main window lifetime ----
    private MainWindow EnsureMain()
    {
        if(main is not null)return main;
        trim.Stop();var created=new MainWindow(null,Library,checkForUpdates:true,offerToolSetup:true,preferences:Preferences,host:this);main=created;
        created.Closed+=(_,_)=>MainClosed(created);
        // An HWND (without showing) lets editors and dialogs use it as owner.
        new WindowInteropHelper(created).EnsureHandle();
        if(update is not null)created.OfferUpdate(update);
        return created;
    }
    internal void ShowMain(string[]? paths=null)=>EnsureMain().AcceptLaunch(paths??[]);
    private void CopiedLink(){var target=EnsureMain();target.OpenCopiedLink();if(!target.IsVisible)MainHidden();}
    /// The main window was hidden to the tray: release it once nothing needs it.
    internal void MainHidden(){releaseRetry.Start();TryRelease();}
    private void TryRelease()
    {
        if(main is null||main.IsVisible){releaseRetry.Stop();return;}
        if(externalActions>0||!main.CanRelease)return;
        releaseRetry.Stop();main.Release();
    }
    private void MainClosed(MainWindow closed)
    {
        if(!ReferenceEquals(main,closed))return;main=null;
        if(!closed.Released&&!KeepsRunning&&!quitting){Quit();return;}
        FileArtwork.ReleaseCache();PdfRasterDocument.Shutdown();drag?.RefreshHook();TrimSoon();AfterWork();
    }

    // ---- drag gesture, Explorer tools wheel ----
    private void OnGlobalDrop(string action,IDataObject data,string? hint)
    {
        var paths=data.GetData(DataFormats.FileDrop) as string[];var payload=DragPayload.Parse(data,hint);
        if(paths is {Length:>0}&&action.StartsWith("convert:",StringComparison.Ordinal)&&main is not {IsVisible:true})
        {
            // Back in the tray: convert headlessly instead of building the main window.
            var format=action[8..];Dispatcher.CurrentDispatcher.BeginInvoke(()=>conversions.Enqueue(format,paths,merge:false));return;
        }
        RunAction(action,paths,payload);
    }
    private void RunAction(string action,string[]? paths,WebMediaPayload? payload)
    {
        externalActions++;
        Dispatcher.CurrentDispatcher.BeginInvoke(async()=>
        {
            try{var target=EnsureMain();await target.RunExternalAsync(action,paths,payload);}
            catch(Exception error){MessageBox.Show(L10n.Text(error.Message),"Nashi",MessageBoxButton.OK,MessageBoxImage.Information);}
            finally{externalActions--;if(main is {IsVisible:false})MainHidden();AfterWork();}
        });
    }
    private void ShowToolsWheel(string[] paths)
    {
        if(paths.Length==0){AfterWork();return;}
        toolsWheel?.Close();
        var tools=FeatureCatalog.Tools(paths);
        IReadOnlyList<ToolChoice> choices=tools.Length>0?tools.Select(t=>new ToolChoice(t,FeatureCatalog.ToolNames[t],MainWindow.ToolSymbol(t))).ToArray():[new("share","Share","◉"),new("float","Float","▣"),new("copy","Copy","▱"),new("move","Move","↗"),new("copyPath","Copy Path","↪"),new("terminal","Terminal",">_")];
        var wheel=new WheelWindow(null,choices,choice=>RunAction(choice.Id,paths,null)){WindowStartupLocation=WindowStartupLocation.Manual,Topmost=true};toolsWheel=wheel;
        wheel.Closed+=(_,_)=>{if(ReferenceEquals(toolsWheel,wheel))toolsWheel=null;Dispatcher.CurrentDispatcher.BeginInvoke(AfterWork,DispatcherPriority.Background);};
        GetCursorPos(out var point);wheel.Show();DesktopGeometry.PlaceWheel(wheel,point.X,point.Y,WheelPresentation.Diameter*Preferences.Value.WheelScale);wheel.Activate();
    }

    // ---- tray ----
    private System.Windows.Controls.ContextMenu TrayMenu()
    {
        var menu=new System.Windows.Controls.ContextMenu();
        void Item(string text,Action action){var item=new System.Windows.Controls.MenuItem();L10n.Bind(item,System.Windows.Controls.HeaderedItemsControl.HeaderProperty,text);item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Item("Open Nashi",()=>ShowMain());Item("Library",()=>{ShowMain();main?.ShowLibrary(true);});Item("Download copied link",CopiedLink);Item("Settings",()=>{ShowMain();main?.OpenSettings();});Item(update is null?"Check for updates":"Update to "+update.Version,()=>{ShowMain();main?.CheckForUpdatesFromTray();});menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Quit Nashi",()=>{if(main?.Busy==true||conversions.Busy){ShowMain();main?.ShowStatus("Finish or cancel the current operation before quitting.");return;}Quit();});return menu;
    }
    internal void Quit(){quitting=true;main?.MarkQuitting();app.Shutdown();}
    internal void UpdateFound(WindowsUpdate candidate){update=candidate;tray?.UpdateAvailable(candidate.Version);}
    private async Task CheckForUpdatesAsync()
    {
        if(checking||update is not null||!Preferences.Value.AutomaticUpdateChecks||main?.Busy==true)return;checking=true;
        try{var found=await PublicUpdates.CheckAsync(UpdateInstaller.Current);if(found is not null){update=found;tray?.UpdateAvailable(found.Version);main?.OfferUpdate(found);}}
        catch(Exception){/* Automatic checks stay silent, as before. */}
        finally{checking=false;TrimSoon();}
    }

    // ---- idle ----
    /// Called whenever some work finished. A transient host exits when nothing
    /// is left; the resident host trims memory a little later.
    private void AfterWork()
    {
        if(!desktop&&!quitting&&!conversions.Busy&&main is null&&toolsWheel is null&&externalActions==0&&wheelPaths.Count==0&&!Application.Current.Windows.OfType<Window>().Any(w=>w.IsVisible)){app.Shutdown();return;}
        if(desktop&&!conversions.Busy)drag?.RefreshHook();
        TrimSoon();
    }
    private void TrimSoon()=>TrimSoon(TimeSpan.FromSeconds(20));
    private void TrimSoon(TimeSpan delay){if(!desktop||main is not null)return;trim.Stop();trim.Interval=delay;trim.Start();}
    /// Returns freed managed memory to Windows and empties the working set.
    internal static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode=GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration,GCCollectionMode.Aggressive,true,true);GC.WaitForPendingFinalizers();
        try{using var self=Process.GetCurrentProcess();SetProcessWorkingSetSizeEx(self.Handle,-1,-1,0);}catch(InvalidOperationException){}
    }
    public void Dispose()
    {
        trim.Stop();releaseRetry.Stop();wheelMerge.Stop();updates.Stop();hookRefresh.Stop();conversions.Cancel();
        drag?.Dispose();windowManagement?.Dispose();tray?.Dispose();window.Dispose();
    }
}

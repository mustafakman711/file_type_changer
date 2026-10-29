using Nashi.Core;

/// Light resident mode and Explorer integration: command-line parsing and the
/// resident-instance messages, headless format routing, and the event-driven
/// drag gesture state machine.
internal static class SystemModeChecks
{
    internal static void Run(Action<bool,string> check,string root)
    {
        var a=Path.Combine(root,"Photo A.png");var b=Path.Combine(root,"Photo B.HEIC");var doc=Path.Combine(root,"Notes.md");var clip=Path.Combine(root,"Clip.mp4");var song=Path.Combine(root,"Song.wav");var folder=Path.Combine(root,"Folder");
        var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase){a,b,doc,clip,song,folder};bool Exists(string p)=>existing.Contains(Path.GetFullPath(p));bool IsFolder(string p)=>string.Equals(p,folder,StringComparison.OrdinalIgnoreCase);
        CommandLine(check,root,a,b,doc,folder,Exists);
        Routing(check,a,b,doc,clip,song,folder,IsFolder);
        Gesture(check);
    }
    private static void CommandLine(Action<bool,string> check,string root,string a,string b,string doc,string folder,Func<string,bool> exists)
    {
        var convert=LaunchCommand.Parse(["--convert","PDF",a,b],exists);
        check(convert.Kind==LaunchKind.Convert&&convert.Format=="pdf"&&convert.Paths.SequenceEqual(new[]{a,b}),"--convert did not parse its format and files");
        check(LaunchCommand.Parse([a,"--convert",".jpg"],exists) is {Kind:LaunchKind.Convert,Format:"jpg"} first&&first.Paths.SequenceEqual(new[]{a}),"--convert must accept files before the option and a dotted format");
        foreach(var bad in new[]{new[]{"--convert"},["--convert","--wheel"],["--convert","exe",a],["--convert","pdf"],["--convert","pdf",Path.Combine(root,"missing.png")],["--convert","pdf","--wheel",a]})
        {
            try{LaunchCommand.Parse(bad,exists);check(false,"Invalid command line accepted: "+string.Join(' ',bad));}catch(ArgumentException){check(true,"");}
        }
        var dropped=LaunchCommand.Parse(["--convert","png",doc,Path.Combine(root,"gone.md"),doc],exists);
        check(dropped.Paths.SequenceEqual(new[]{doc}),"Missing or duplicate files were not dropped");
        var list=new[]{a,"",b+"  ",Path.Combine(root,"gone.png")};
        var fromList=LaunchCommand.Parse(["--convert","webp","--files-from","list.txt"],exists,_=>list);
        check(fromList.Paths.SequenceEqual(new[]{a,b}),"--files-from did not read the Explorer selection list");
        check(LaunchCommand.Parse(["--wheel",folder,a],exists) is {Kind:LaunchKind.Wheel,Format:null} wheel&&wheel.Paths.Length==2,"--wheel did not keep files and folders");
        check(LaunchCommand.Parse(["--background"],exists).Kind==LaunchKind.Background,"Sign-in launch must stay in the tray");
        check(LaunchCommand.Parse([a],exists) is {Kind:LaunchKind.Open} open&&open.Paths.Single()==a,"A plain launch must still open its files");
        check(LaunchCommand.Parse([],exists) is {Kind:LaunchKind.Open,Paths.Length:0},"An empty launch must open Nashi");
        check(LaunchCommand.NormalizeFormat("TXT")=="txt"&&LaunchCommand.NormalizeFormat("exe") is null&&LaunchCommand.NormalizeFormat("") is null,"Output formats must come from the shared conversion matrix");

        // Messages to the resident instance.
        var many=Enumerable.Range(0,600).Select(n=>Path.Combine(root,$"Many {n}.png")).ToArray();var all=new HashSet<string>(many.Append(a),StringComparer.OrdinalIgnoreCase);
        var batches=new LaunchCommand(LaunchKind.Convert,"jpg",many).ToMessages();
        check(batches.Count==3&&batches.All(m=>m.Length<=LaunchCommand.MessagePathLimit+2&&m.Length<=256),"Large selections must be split under the single-instance message limit");
        var received=batches.Select(m=>LaunchCommand.FromMessage(m,all.Contains)).ToArray();
        check(received.All(r=>r.Kind==LaunchKind.Convert&&r.Format=="jpg")&&received.SelectMany(r=>r.Paths).SequenceEqual(many),"Convert messages lost files or their format");
        // The Explorer menu sends a whole selection: only its last chunk starts the job at once.
        var whole=(new LaunchCommand(LaunchKind.Convert,"jpg",many){Whole=true}).ToMessages().Select(m=>LaunchCommand.FromMessage(m,all.Contains)).ToArray();
        check(whole.Length==3&&whole.Select(r=>r.Whole).SequenceEqual(new[]{false,false,true})&&whole.All(r=>r.Kind==LaunchKind.Convert&&r.Format=="jpg")&&whole.SelectMany(r=>r.Paths).SequenceEqual(many),"A whole Explorer selection must keep its files and start only after its last chunk");
        check(!received.Any(r=>r.Whole)&&LaunchCommand.Parse(["--convert","jpg",a],exists).Whole==false,"Command-line conversions must keep merging per-file Explorer launches");
        check(LaunchCommand.FromMessage(LaunchCommand.Open(a).ToMessages().Single(),exists) is {Kind:LaunchKind.Open} plain&&plain.Paths.Single()==a&&LaunchCommand.Open(a).ToMessages().Single().SequenceEqual(new[]{a}),"Plain launches must keep the original message format");
        check(LaunchCommand.FromMessage(new LaunchCommand(LaunchKind.Wheel,null,[folder]).ToMessages().Single(),exists).Kind==LaunchKind.Wheel,"Tools wheel request was not recognised");
        check(LaunchCommand.FromMessage(new LaunchCommand(LaunchKind.Background,null,[]).ToMessages().Single(),exists).Kind==LaunchKind.Background,"A second sign-in launch must not open the window");
        check(LaunchCommand.FromMessage(["nashi:convert","exe",a],exists).Kind==LaunchKind.Background&&LaunchCommand.FromMessage(["nashi:future",a],exists).Kind==LaunchKind.Background,"Unknown or unsafe commands must be ignored");
        check(LaunchCommand.FromMessage(["nashi:convert","pdf","relative.png",a],exists).Paths.SequenceEqual(new[]{a}),"Forwarded conversions must only accept full paths");
    }
    private static void Routing(Action<bool,string> check,string a,string b,string doc,string clip,string song,string folder,Func<string,bool> isFolder)
    {
        check(ConversionRoute.Engine([a,b],"jpg",isFolder)==ConversionEngine.Image,"Photos must use the image engine");
        check(ConversionRoute.Engine([clip],"mp3",isFolder)==ConversionEngine.Media&&ConversionRoute.Engine([song],"mp3",isFolder)==ConversionEngine.Media,"Video and audio must use the media engine");
        check(ConversionRoute.Engine([doc],"pdf",isFolder)==ConversionEngine.Document,"Documents must use document conversion");
        check(ConversionRoute.Engine([a],"pdf",isFolder)==ConversionEngine.Image,"Photo to PDF stays on the image engine like the main window");
        check(ConversionRoute.Engine([a],"mp3",isFolder)==ConversionEngine.None&&ConversionRoute.Engine([folder],"zip",isFolder)==ConversionEngine.None,"Impossible conversions must be refused");
        check(ConversionRoute.Engine([a,clip],"gif",isFolder)==ConversionEngine.None,"Mixed selections must follow the shared matrix");
        var plan=ConversionRoute.Plan([a,doc,b,a],"pdf",isFolder);
        check(plan.Skipped.Count==0&&plan.Jobs.Count==2&&plan.Jobs[0].SequenceEqual(new[]{a,b})&&plan.Jobs[1].SequenceEqual(new[]{doc}),"Photos and documents becoming PDF must run on their own engines, every file once");
        var partial=ConversionRoute.Plan([a,b,song],"jpg",isFolder);
        check(partial.Jobs.Count==1&&partial.Jobs[0].SequenceEqual(new[]{a,b})&&partial.Skipped.SequenceEqual(new[]{song}),"Files that cannot become the format must be skipped, not fail the batch");
        var mixed=ConversionRoute.Plan([a,clip],"gif",isFolder);
        check(mixed.Jobs.Single().SequenceEqual(new[]{clip})&&mixed.Skipped.SequenceEqual(new[]{a}),"Video to GIF must not take a photo along");
        check(ConversionRoute.Plan([a,doc],"jpg",isFolder).Jobs.Select(j=>ConversionRoute.Engine(j,"jpg",isFolder)).SequenceEqual(new[]{ConversionEngine.Image,ConversionEngine.Document}),"A photo and a document to JPG must each use their own engine");
        foreach(var (input,targets) in FeatureCatalog.Conversions)foreach(var target in targets)
            check(ConversionRoute.Engine(["File."+input],target,_=>false)!=ConversionEngine.None||input is "pages" or "rtfd","Catalog route has no engine: "+input+" -> "+target);
        check(FeatureCatalog.Formats([folder],isFolder).Length==0&&FeatureCatalog.Formats([Path.ChangeExtension(folder,".pages")],p=>true).Length>0,"Folders (except bundles) have no conversions");
    }
    private static void Gesture(Action<bool,string> check)
    {
        var p=new Preferences();var g=new DragGesture();
        check(!g.Tracking&&!g.Armed&&g.Evaluate("Shift",p) is null,"An idle gesture must not evaluate modifiers");
        g.Press(100,100);check(g.Tracking&&!g.Armed,"A press alone must not arm modifier checks");
        check(!g.Move(104,105,true)&&!g.Armed,"Moves under the 10 px threshold must not arm");
        check(g.Evaluate("Shift",p) is null,"Modifiers before the threshold must not open a wheel");
        check(g.Move(106,104,true)&&g.Armed,"Crossing the threshold with the primary button must arm checks");
        check(!g.Move(300,300,true),"The threshold must be reported only once");
        check(g.Evaluate("",p) is null&&g.Evaluate("Ctrl+Alt",p) is null&&g.Armed,"Unmapped chords keep checking");
        check(g.Evaluate("Shift",p)=="convert"&&!g.Armed&&g.Phase==DragPhase.Opened,"Shift must open the conversion wheel once");
        check(g.Evaluate("Ctrl",p) is null,"A second wheel must not open in the same drag");
        check(g.Release()&&!g.Tracking,"Releasing must dismiss an opened wheel and stop tracking");
        check(!g.Release(),"A stray release must be harmless");
        g.Press(0,0);g.Move(0,12,true);check(g.Evaluate("Ctrl",p)=="tools","Ctrl opens tools");g.Release();
        g.Press(0,0);g.Move(-12,0,true);check(g.Evaluate("Alt",p)=="actions","Alt opens file actions");g.Release();
        g.Press(0,0);check(!g.Move(20,0,false)&&g.Phase==DragPhase.Ignored&&g.Evaluate("Shift",p) is null,"A secondary-button drag must be ignored");check(!g.Release()&&g.Phase==DragPhase.Idle,"Ignored drags end on release");
        g.Press(0,0);g.Move(10,0,true);check(!g.Release()&&g.Evaluate("Shift",p) is null,"A drag released before a modifier must not open later");
        var custom=p with{ToolsModifier="Ctrl+Shift",ConvertModifier="Alt",ActionsModifier="Shift"};
        check(DragGesture.Kind(DragGesture.Chord(true,true,false),custom)=="tools"&&DragGesture.Kind("Alt",custom)=="convert"&&DragGesture.Kind("Shift",custom)=="actions","Customized chords must map like Settings");
        check(DragGesture.Chord(true,true,true)=="Ctrl+Shift+Alt"&&DragGesture.Chord(false,false,false)=="","Chord names must match the Settings spelling");
    }
}

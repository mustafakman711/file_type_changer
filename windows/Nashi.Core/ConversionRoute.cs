namespace Nashi.Core;

public enum ConversionEngine{None,Image,Media,Document}

/// The engine choice the main window makes for "Convert to FORMAT", shared
/// with headless (Explorer menu / command line) conversions.
public static class ConversionRoute
{
    public static ConversionEngine Engine(IReadOnlyList<string> inputs,string format,Func<string,bool>? isDirectory=null)
    {
        if(inputs.Count==0||!FeatureCatalog.Formats(inputs,isDirectory).Contains(format))return ConversionEngine.None;
        if(inputs.All(FeatureCatalog.IsImage))return ConversionEngine.Image;
        if(inputs.All(FeatureCatalog.IsVideo)||inputs.All(FeatureCatalog.IsAudio))return ConversionEngine.Media;
        return ConversionEngine.Document;
    }

    /// Splits a request into jobs the engines accept: photos, video, audio and
    /// documents run as separate jobs (each a single batch when possible), and
    /// files that cannot become FORMAT are reported instead of failing the batch.
    public static (IReadOnlyList<string[]> Jobs,IReadOnlyList<string> Skipped) Plan(IReadOnlyList<string> inputs,string format,Func<string,bool>? isDirectory=null)
    {
        var jobs=new List<string[]>();var skipped=new List<string>();
        foreach(var kind in inputs.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(Kind))
        {
            var files=kind.ToArray();
            if(Engine(files,format,isDirectory)!=ConversionEngine.None){jobs.Add(files);continue;}
            foreach(var group in files.GroupBy(FeatureCatalog.Extension))
            {
                var same=group.ToArray();
                if(Engine(same,format,isDirectory)!=ConversionEngine.None)jobs.Add(same);else skipped.AddRange(same);
            }
        }
        return(jobs,skipped);
    }
    private static int Kind(string path)=>FeatureCatalog.IsImage(path)?0:FeatureCatalog.IsVideo(path)?1:FeatureCatalog.IsAudio(path)?2:3;
}

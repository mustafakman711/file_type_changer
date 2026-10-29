using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Nashi.Core;

public static class AppLocalization
{
    public static string Normalize(string? language)=>language=="tr"?"tr":"en";
    public static bool Matches(string source,string query,string language)
    {
        var compare=CultureInfo.GetCultureInfo(Normalize(language)).CompareInfo;
        return compare.IndexOf(source,query,CompareOptions.IgnoreCase|CompareOptions.IgnoreNonSpace)>=0||compare.IndexOf(Text(source,language),query,CompareOptions.IgnoreCase|CompareOptions.IgnoreNonSpace)>=0;
    }
    public static IReadOnlyDictionary<string,string> Turkish{get;}=Load();
    private static Dictionary<string,string> Load()
    {
        using var stream=typeof(AppLocalization).Assembly.GetManifestResourceStream("Nashi.Turkish.json")??throw new InvalidDataException("Missing Turkish UI catalog.");
        var catalog=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??throw new InvalidDataException("Invalid Turkish UI catalog.");
        foreach(var (source,target) in WindowsOnly)catalog.TryAdd(source,target);return catalog;
    }
    // Windows-only shell integration copy. Kept here rather than in the shared
    // catalog, whose macOS string tables are generated from it. A property, so
    // static initialization order cannot leave it empty when Load() runs.
    private static (string,string)[] WindowsOnly=>
    [
        ("Convert with Nashi","Nashi ile dönüştür"),
        ("Nashi tools…","Nashi araçları…"),
        ("Show Nashi in Explorer’s right-click menu","Nashi’yi Dosya Gezgini sağ tık menüsünde göster"),
        ("{0} files can’t become {1} and were skipped.","{0} dosya {1} biçimine dönüştürülemediği için atlandı."),
        ("Choose at least one existing file to convert.","Dönüştürmek için en az bir mevcut dosya seç."),
        ("Unknown output format: {0}","Bilinmeyen çıktı biçimi: {0}"),
        ("Use --convert FORMAT FILE...","--convert BİÇİM DOSYA... biçiminde kullan"),
        ("Nashi is running as administrator, so Windows blocks drops.\nQuit Nashi and open it normally.","Nashi yönetici olarak çalışıyor, Windows bırakmaları engelliyor.\nNashi’den çıkıp normal şekilde aç."),
    ];
    // Product and key names that keep Latin capitals in Turkish upper case (SHIFT, not SHİFT). Declared before `uppercase`, which uses it.
    public static IReadOnlySet<string> LatinWords{get;}=LoadLatinWords();
    private static HashSet<string> LoadLatinWords()
    {
        using var stream=typeof(AppLocalization).Assembly.GetManifestResourceStream("Nashi.LatinWords.json")??throw new InvalidDataException("Missing Latin word list.");
        return JsonSerializer.Deserialize<HashSet<string>>(stream)??throw new InvalidDataException("Invalid Latin word list.");
    }
    public static string Uppercase(string value,string language)
    {
        if(Normalize(language)!="tr")return value.ToUpperInvariant();
        var turkish=CultureInfo.GetCultureInfo("tr-TR");
        return Regex.Replace(value,@"\p{L}+",word=>LatinWords.Contains(word.Value)?word.Value.ToUpperInvariant():word.Value.ToUpper(turkish));
    }
    private static readonly Dictionary<string,string> uppercase=Turkish.Where(x=>!x.Key.Contains("{0}",StringComparison.Ordinal)).GroupBy(x=>x.Key.ToUpperInvariant()).ToDictionary(g=>g.Key,g=>Uppercase(g.First().Value,"tr"));
    private static readonly (Regex Pattern,string Target)[] templates=Turkish.Where(x=>x.Key.Contains("{0}",StringComparison.Ordinal)).OrderByDescending(x=>x.Key.Length).Select(x=>
    {
        var pattern=Regex.Escape(x.Key);
        for(var i=0;i<10;i++)pattern=pattern.Replace(Regex.Escape("{"+i+"}"),"(.*?)",StringComparison.Ordinal);
        return(new Regex("^"+pattern+"$",RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)),x.Value);
    }).ToArray();
    public static string Text(string source,string language)
    {
        if(Normalize(language)!="tr")return source;
        if(Turkish.TryGetValue(source,out var exact)||uppercase.TryGetValue(source,out exact))return exact;
        if(source.Length>=16_384)return source;
        foreach(var (pattern,target) in templates)
        {
            var match=pattern.Match(source);if(!match.Success)continue;
            return Regex.Replace(target,@"\{([0-9])\}",token=>int.TryParse(token.Groups[1].Value,out var index)&&index+1<match.Groups.Count?match.Groups[index+1].Value:token.Value);
        }
        return source;
    }
}

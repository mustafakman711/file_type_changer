using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nashi.Core;
namespace Nashi.Windows;

/// PDF → PowerPoint without Office or LibreOffice. Upright, visible text with
/// a reliable Unicode mapping becomes editable text boxes; everything else
/// (artwork, photos, vector shapes, rotated or unmappable text) stays in a
/// rendered page picture behind it, so the slide looks like the page.
internal static class PdfSlides
{
    internal sealed record Glyph(char Value,double X,double Baseline,double Right,double Size,string Font,bool Bold,bool Italic,uint Color,bool SpaceBefore);

    internal static Task WriteAsync(string input,string output,int dpi,string password,CancellationToken token)=>Task.Run(()=>
    {
        // PDFium renders one page at a time; finding the editable lines and
        // encoding the page picture run in parallel with the next render.
        using var pdf=PdfRasterDocument.Open(input,password);var pending=new List<Task<SlidePage>>(pdf.PageCount);using var slots=new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount/2,1,6));
        try
        {
            for(var i=0;i<pdf.PageCount;i++)
            {
                slots.Wait(token);var raw=pdf.RenderSlide(i,dpi,token);
                pending.Add(Task.Run(()=>{try{return Finish(raw);}finally{slots.Release();}},token));
            }
        }
        finally{try{Task.WaitAll(pending.ToArray<Task>());}catch(AggregateException){}}
        token.ThrowIfCancellationRequested();var failure=pending.FirstOrDefault(p=>p.IsFaulted)?.Exception?.InnerException;if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        var pages=pending.Select(p=>p.Result).ToArray();if(pages.Sum(p=>p.Background.LongLength)>512_000_000)throw new InvalidDataException("Rendered pages exceed 512 MB. Choose a lower DPI or split the PDF first.");
        PptxWriter.Write(output,pages,Path.GetFileNameWithoutExtension(input),token);
    },token);

    internal sealed record RawSlide(double Width,double Height,byte[] Pixels,byte[]? Full,int PixelWidth,int PixelHeight,int Dpi,List<List<Glyph>> Glyphs);
    private static SlidePage Finish(RawSlide raw)
    {
        var lines=raw.Full is null?[]:Lines(raw.Glyphs,raw.Full,raw.Pixels,raw.PixelWidth,raw.PixelHeight,raw.Dpi/72d);
        var image=BitmapSource.Create(raw.PixelWidth,raw.PixelHeight,raw.Dpi,raw.Dpi,PixelFormats.Bgra32,null,raw.Pixels,raw.PixelWidth*4);image.Freeze();
        return new SlidePage(raw.Width,raw.Height,Encode(image,out var extension),extension,lines);
    }

    private static readonly Dictionary<string,FontFamily> families=new(StringComparer.Ordinal);
    private static FontFamily Family(string name){_=installed.Value;return families.TryGetValue(name,out var family)?family:new FontFamily(name);}
    private static readonly Lazy<Dictionary<string,string>> installed=new(()=>
    {
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        try{foreach(var family in Fonts.SystemFontFamilies)foreach(var name in family.FamilyNames.Values)if(names.TryAdd(Compact(name),name))families.TryAdd(name,family);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException){}
        // Microsoft 365 cloud fonts (Aptos, Montserrat, …) are usable by
        // PowerPoint but not installed in Windows; read their metrics directly.
        try
        {
            var cloud=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","FontCache","4","CloudFonts");
            if(Directory.Exists(cloud))foreach(var folder in Directory.EnumerateDirectories(cloud))foreach(var family in Fonts.GetFontFamilies(new Uri(folder+Path.DirectorySeparatorChar)))foreach(var name in family.FamilyNames.Values)if(names.TryAdd(Compact(name),name))families.TryAdd(name,family);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException){}
        return names;
    });
    private static string Compact(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static readonly string[] weights=["thin","hairline","extralight","ultralight","light","book","medium","semibold","demibold","extrabold","ultrabold","black","heavy","condensed","narrow"];
    private static readonly Dictionary<string,string> metricTwins=new(StringComparer.Ordinal){["helvetica"]="Arial",["helveticaneue"]="Arial",["arimo"]="Arial",["liberationsans"]="Arial",["nimbussans"]="Arial",["times"]="Times New Roman",["timesroman"]="Times New Roman",["tinos"]="Times New Roman",["liberationserif"]="Times New Roman",["nimbusroman"]="Times New Roman",["courier"]="Courier New",["cousine"]="Courier New",["liberationmono"]="Courier New",["nimbusmono"]="Courier New",["symbol"]="Symbol"};

    /// Maps a PDF base font name such as "ABCDEF+Montserrat-SemiBoldItalic" or
    /// "Arial,Bold" to an installed family plus bold/italic flags. Fonts that
    /// are not installed keep their name unless a metric twin is installed.
    internal static (string Family,bool Bold,bool Italic) Font(string name,int weight,int flags)
    {
        var plus=name.IndexOf('+');if(plus==6)name=name[(plus+1)..];
        var cut=name.IndexOfAny([',','-']);var family=cut>0?name[..cut]:name;var style=cut>0?name[(cut+1)..]:"";
        foreach(var suffix in new[]{"PSMT","PS","MT"})if(family.EndsWith(suffix,StringComparison.Ordinal)&&family.Length>suffix.Length+1){family=family[..^suffix.Length];break;}
        var lowerStyle=style.ToLowerInvariant();if(cut<0)foreach(var word in new[]{"bolditalic","bold","italic","oblique"}.Concat(weights))if(Compact(family).EndsWith(word,StringComparison.Ordinal)&&Compact(family).Length>word.Length+2){lowerStyle=word+lowerStyle;family=family[..^word.Length].TrimEnd(' ','_');break;}
        var italic=lowerStyle.Contains("italic")||lowerStyle.Contains("oblique")||(flags&0x40)!=0;
        var bold=lowerStyle.Replace("semibold","").Replace("demibold","").Replace("extrabold","").Replace("ultrabold","").Contains("bold")||(flags&0x40000)!=0;
        var weightWord=weights.FirstOrDefault(w=>Compact(lowerStyle).Contains(w,StringComparison.Ordinal));
        if(weightWord is not null&&installed.Value.TryGetValue(Compact(family)+weightWord,out var weighted))return(weighted,bold&&weightWord is not ("extrabold" or "ultrabold" or "black" or "heavy"),italic);
        if(weightWord is "semibold" or "demibold" or "extrabold" or "ultrabold" or "black" or "heavy"||weight>=600)bold=true;
        if(installed.Value.TryGetValue(Compact(family),out var known))return(known,bold,italic);
        if(metricTwins.TryGetValue(Compact(family),out var twin))return(twin,bold,italic);
        // Keep the name of other fonts: Office may have it (cloud fonts such
        // as Aptos) and it travels with the file to machines that do.
        var readable=new StringBuilder();foreach(var c in family){if(readable.Length>0&&char.IsUpper(c)&&char.IsLower(readable[^1]))readable.Append(' ');readable.Append(c);}
        return(readable.Length==0?"Arial":readable.ToString(),bold,italic);
    }

    private static readonly Dictionary<string,double> ascents=[];
    // Measured in PowerPoint: with a 1.2 em line pitch the first baseline sits
    // 1.2 em minus the font's descent below the box top (within ~0.01 em for
    // common fonts). Unknown fonts use the writer's typical value.
    private static double Ascent(string font)
    {
        lock(ascents)
        {
            if(ascents.TryGetValue(font,out var known))return known;
            double value=0;if(installed.Value.ContainsKey(Compact(font))){var family=Family(font);var descent=family.LineSpacing-family.Baseline;if(descent is > 0 and < .6)value=1.2-descent;}
            return ascents[font]=value;
        }
    }
    private static readonly Dictionary<(string,bool,bool),GlyphTypeface?> faces=[];
    private static double? Advance(char value,string font,bool bold,bool italic)
    {
        GlyphTypeface? face;
        lock(faces)
        {
            if(!faces.TryGetValue((font,bold,italic),out face)){if(!new Typeface(Family(font),italic?FontStyles.Italic:FontStyles.Normal,bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal).TryGetGlyphTypeface(out face))face=null;faces[(font,bold,italic)]=face;}
            return face is not null&&face.CharacterToGlyphMap.TryGetValue(value,out var glyph)?face.AdvanceWidths[glyph]:null;
        }
    }

    /// Turns glyph lines into slide text. Lines whose glyphs are not visible
    /// on the page (painted over later, or invisible) are dropped, and each
    /// line gets the character spacing that makes it as wide as on the page.
    internal static List<SlideTextLine> Lines(List<List<Glyph>> source,byte[] full,byte[] background,int width,int height,double pixelsPerPoint)
    {
        var lines=new List<SlideTextLine>();
        // Each glyph's band between baseline and cap height. Pixels inside
        // several lines' bands are ambiguous and ignored, so a hidden line is
        // not mistaken for visible because a neighbour's text changed there.
        (int X0,int Y0,int X1,int Y1) Band(Glyph g)=>(Math.Clamp((int)Math.Floor(g.X*pixelsPerPoint),0,width),Math.Clamp((int)Math.Floor((g.Baseline-g.Size*.68)*pixelsPerPoint),0,height),Math.Clamp((int)Math.Ceiling(g.Right*pixelsPerPoint),0,width),Math.Clamp((int)Math.Ceiling(g.Baseline*pixelsPerPoint),0,height));
        var owners=new byte[width*height];var stamp=new int[width*height];var serial=0;
        foreach(var glyphs in source){serial++;foreach(var g in glyphs){var b=Band(g);for(var y=b.Y0;y<b.Y1;y++)for(var x=b.X0;x<b.X1;x++){var p=y*width+x;if(stamp[p]==serial)continue;stamp[p]=serial;if(owners[p]<255)owners[p]++;}}}
        serial=0;Array.Clear(stamp);
        foreach(var glyphs in source)
        {
            serial++;if(glyphs.Count==0)continue;
            long changed=0,area=0;
            foreach(var g in glyphs){var b=Band(g);for(var y=b.Y0;y<b.Y1;y++)for(var x=b.X0;x<b.X1;x++){var p=y*width+x;if(stamp[p]==serial||owners[p]!=1)continue;stamp[p]=serial;area++;var o=p*4;if(Math.Abs(full[o]-background[o])+Math.Abs(full[o+1]-background[o+1])+Math.Abs(full[o+2]-background[o+2])>48)changed++;}}
            // Text painted over later (or drawn invisibly) is not visible on
            // the page, so it is left out of the editable layer.
            if(area>0&&changed<Math.Max(2,area/50))continue;
            // Word spaces: explicit spaces, or gaps clearly wider than the
            // line's usual letter gap (tracking).
            var gaps=new List<double>();for(var k=1;k<glyphs.Count;k++)if(!glyphs[k].SpaceBefore)gaps.Add(glyphs[k].X-glyphs[k-1].Right);gaps.Sort();var tracking=gaps.Count>=2?gaps[gaps.Count/2]:0;
            // A gap clearly wider than a word space (table cells, tab stops)
            // starts a separate box so every column keeps its position.
            var start=0;
            for(var k=1;k<=glyphs.Count;k++)
            {
                if(k<glyphs.Count){var previous=glyphs[k-1];var space=(Advance(' ',previous.Font,previous.Bold,previous.Italic)??.3)*previous.Size;if(glyphs[k].X-previous.Right-tracking<=space+previous.Size*.25)continue;}
                Emit(glyphs.GetRange(start,k-start),tracking);start=k;
            }
        }
        return lines;
        void Emit(List<Glyph> glyphs,double tracking)
        {
            var size=glyphs.Max(g=>g.Size);var left=glyphs[0].X;var right=glyphs.Max(g=>g.Right);var baseline=glyphs[0].Baseline;
            var runs=new List<SlideTextRun>();var text=new StringBuilder();Glyph? style=null;double natural=0;var characters=0;var measurable=true;
            void Flush(){if(text.Length>0&&style is not null)runs.Add(new(text.ToString(),style.Font,style.Size,style.Bold,style.Italic,style.Color));text.Clear();}
            void Add(char value,Glyph glyph){text.Append(value);characters++;if(Advance(value,glyph.Font,glyph.Bold,glyph.Italic) is {} advance)natural+=advance*glyph.Size;else measurable=false;}
            for(var k=0;k<glyphs.Count;k++)
            {
                var glyph=glyphs[k];
                if(k>0&&(glyph.SpaceBefore||glyph.X-glyphs[k-1].Right-tracking>glyph.Size*.15))Add(' ',style!);
                if(style is null||style.Font!=glyph.Font||Math.Abs(style.Size-glyph.Size)>.05||style.Bold!=glyph.Bold||style.Italic!=glyph.Italic||style.Color!=glyph.Color){Flush();style=glyph;}
                Add(glyph.Value,glyph);
            }
            Flush();
            // Character spacing that makes the line exactly as wide as on the
            // page with the font PowerPoint will use (also reproduces tracking).
            var spacing=measurable&&characters>1?Math.Clamp((right-left-natural)/characters,-size*.3,size*.6):tracking;
            if(Math.Abs(spacing)<.005)spacing=0;
            // The line's deepest descent decides where PowerPoint puts the baseline.
            var ascent=runs.Min(r=>Ascent(r.Font) is var a&&a>0?a:PptxWriter.BaselineOffset);
            lines.Add(new(left,baseline,right-left,runs.Select(r=>r with{Spacing=spacing}).ToArray(),ascent));
        }
    }

    internal static byte[] Encode(BitmapSource image,out string extension)
    {
        static byte[] Save(BitmapEncoder encoder,BitmapSource source){encoder.Frames.Add(BitmapFrame.Create(source));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();}
        // Flat artwork stays lossless; photographic pages use JPEG when PNG
        // would be much larger.
        var png=Save(new PngBitmapEncoder(),image);var jpeg=Save(new JpegBitmapEncoder{QualityLevel=92},image);
        if(png.Length<=jpeg.Length*1.5){extension="png";return png;}extension="jpeg";return jpeg;
    }
}

internal sealed partial class PdfRasterDocument
{
    internal PdfSlides.RawSlide RenderSlide(int index,int dpi,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(dpi<36||dpi>600)throw new ArgumentOutOfRangeException(nameof(dpi));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(document==0,this);
            if(index<0||index>=PageCount)throw new ArgumentOutOfRangeException(nameof(index));
            var page=Native.FPDF_LoadPage(document,index);if(page==0)throw new InvalidDataException("This PDF page could not be read.");
            var hidden=new List<nint>();
            try
            {
                form?.Load(page,index);
                var widthPoints=Native.FPDF_GetPageWidth(page);var heightPoints=Native.FPDF_GetPageHeight(page);
                var w=widthPoints*dpi/72;var h=heightPoints*dpi/72;
                if(!double.IsFinite(w)||!double.IsFinite(h)||w<1||h<1||w*h>100_000_000)throw new InvalidDataException("PDF page exceeds 100 MP.");
                var width=checked((int)Math.Ceiling(w));var height=checked((int)Math.Ceiling(h));
                var editable=new HashSet<nint>();var glyphs=Native.FPDFPage_GetRotation(page)==0?ExtractText(page,widthPoints,heightPoints,editable,token):[];
                byte[]? full=null;
                if(glyphs.Count>0){full=Render(page,width,height);foreach(var item in editable)if(Native.FPDFPageObj_SetIsActive(item,0)!=0)hidden.Add(item);}
                var pixels=Render(page,width,height);token.ThrowIfCancellationRequested();
                return new(widthPoints,heightPoints,pixels,full,width,height,dpi,glyphs);
            }
            finally{foreach(var item in hidden)Native.FPDFPageObj_SetIsActive(item,1);form?.ClosePage();Native.FPDF_ClosePage(page);}
        }
    }

    private byte[] Render(nint page,int width,int height)
    {
        var stride=checked(width*4);var pixels=new byte[checked(stride*height)];var pinned=GCHandle.Alloc(pixels,GCHandleType.Pinned);nint bitmap=0;
        try
        {
            bitmap=Native.FPDFBitmap_CreateEx(width,height,4,pinned.AddrOfPinnedObject(),stride);
            if(bitmap==0)throw new InvalidDataException("PDF preview bitmap could not be allocated.");
            if(Native.FPDFBitmap_FillRect(bitmap,0,0,width,height,0xffffffff)==0)throw new InvalidDataException("PDF preview background could not be prepared.");
            Native.FPDF_RenderPageBitmap(bitmap,page,0,0,width,height,0,1);form?.Draw(bitmap,width,height);return pixels;
        }
        finally{if(bitmap!=0)Native.FPDFBitmap_Destroy(bitmap);pinned.Free();}
    }

    private static List<List<PdfSlides.Glyph>> ExtractText(nint page,double widthPoints,double heightPoints,HashSet<nint> editable,CancellationToken token)
    {
        var text=Native.FPDFText_LoadPage(page);if(text==0)return[];
        try
        {
            var count=Native.FPDFText_CountChars(text);if(count<=0)return[];
            // Text objects are moved to the editable layer only when every
            // glyph they draw can be reproduced faithfully as live text.
            var objects=new nint[count];var rejected=new HashSet<nint>();var emboldened=new HashSet<nint>();var fonts=new Dictionary<(string,int,int),(string,bool,bool)>();var name=new byte[512];
            const int scale=64;var deviceWidth=checked((int)Math.Ceiling(widthPoints*scale));var deviceHeight=checked((int)Math.Ceiling(heightPoints*scale));
            for(var i=0;i<count;i++)
            {
                if(Native.FPDFText_IsGenerated(text,i)==1)continue;var item=Native.FPDFText_GetTextObject(text,i);objects[i]=item;if(item==0)continue;
                var unicode=Native.FPDFText_GetUnicode(text,i);var mode=Native.FPDFTextObj_GetTextRenderMode(item);
                var upright=Native.FPDFText_GetMatrix(text,i,out var m)!=0&&m.A>0&&m.D>0&&Math.Abs(m.B)<=Math.Abs(m.A)*.001&&Math.Abs(m.C)<=Math.Abs(m.D)*.001&&Math.Abs(m.A/m.D-1)<.08;
                var inside=Native.FPDFText_GetCharOrigin(text,i,out var ox,out var oy)!=0&&Native.FPDF_PageToDevice(page,0,0,deviceWidth,deviceHeight,0,ox,oy,out var dx,out var dy)!=0&&dx>=0&&dy>=0&&dx<=deviceWidth&&dy<=deviceHeight;
                var mapped=Native.FPDFText_HasUnicodeMapError(text,i)!=1&&unicode is >= 0x20 and <= 0xFFFF and not 0xFFFD and not 0xFFFE and not 0xFFFF and not(>=0xE000 and <=0xF8FF)and not(>=0xD800 and <=0xDFFF);
                var size=Native.FPDFText_GetFontSize(text,i);
                // Fill+stroke is how PDF producers fake bold for fonts without a bold face.
                if(mode==2)emboldened.Add(item);
                if(mode is not(0 or 2)||!upright||!inside||!mapped||!double.IsFinite(size)||size<=0)rejected.Add(item);
            }
            var lines=new List<List<PdfSlides.Glyph>>();var line=new List<PdfSlides.Glyph>();var space=false;
            void End(){if(line.Count>0)lines.Add(line);line=[];space=false;}
            for(var i=0;i<count;i++)
            {
                token.ThrowIfCancellationRequested();var unicode=Native.FPDFText_GetUnicode(text,i);
                // Generated spaces are ignored: PDFium also generates them between the
                // letters of tracked text. Word gaps are measured instead.
                if(Native.FPDFText_IsGenerated(text,i)==1){if(unicode!=' ')End();continue;}
                var item=objects[i];if(item==0||rejected.Contains(item)){End();continue;}
                if(unicode==' '){space=true;continue;}
                Native.FPDFText_GetCharOrigin(text,i,out var ox,out var oy);Native.FPDF_PageToDevice(page,0,0,deviceWidth,deviceHeight,0,ox,oy,out var dx,out var dy);
                var x=dx/(double)scale;var baseline=dy/(double)scale;var size=Native.FPDFText_GetFontSize(text,i);
                // The loose box spans the glyph's advance width, so gaps
                // between boxes measure letter and word spacing.
                var right=x+size*.5;if(Native.FPDFText_GetLooseCharBox(text,i,out var box)!=0&&box.Right>box.Left&&Native.FPDF_PageToDevice(page,0,0,deviceWidth,deviceHeight,0,box.Right,oy,out var rx,out _)!=0)right=rx/(double)scale;
                var length=Native.FPDFText_GetFontInfo(text,i,name,(uint)name.Length,out var flags);var raw=length>1?Encoding.UTF8.GetString(name,0,(int)Math.Min(length-1,(uint)name.Length)):"";var weight=Native.FPDFText_GetFontWeight(text,i);
                if(!fonts.TryGetValue((raw,weight,flags),out var font))fonts[(raw,weight,flags)]=font=PdfSlides.Font(raw,weight,flags);
                var color=Native.FPDFText_GetFillColor(text,i,out var r,out var g,out var b,out var a)!=0?(a<<24)|(r<<16)|(g<<8)|b:0xFF000000;
                // A new line starts on a baseline change or a gap wider than one em
                // (table columns, tab stops), so each box keeps its own position.
                if(line.Count>0)
                {
                    var last=line[^1];var tolerance=Math.Min(size,last.Size)*.3;var gap=x-last.Right;var largest=Math.Max(size,last.Size);
                    if(Math.Abs(baseline-last.Baseline)>tolerance||gap<-largest*.5||gap>largest){var pending=space;End();space=pending;}
                }
                line.Add(new(Convert.ToChar(unicode),x,baseline,right,size,font.Item1,font.Item2||emboldened.Contains(item),font.Item3,color,space&&line.Count>0));space=false;
                editable.Add(item);
            }
            End();
            return lines;
        }
        finally{Native.FPDFText_ClosePage(text);}
    }

    [StructLayout(LayoutKind.Sequential)]private struct Matrix{public float A,B,C,D,E,F;}
    [StructLayout(LayoutKind.Sequential)]private struct RectF{public float Left,Top,Right,Bottom;}
    private static partial class Native
    {
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetLooseCharBox(nint text,int index,out RectF box);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFPage_GetRotation(nint page);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDF_PageToDevice(nint page,int startX,int startY,int sizeX,int sizeY,int rotate,double pageX,double pageY,out int deviceX,out int deviceY);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern void FPDFText_ClosePage(nint text);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_CountChars(nint text);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern uint FPDFText_GetUnicode(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_IsGenerated(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_HasUnicodeMapError(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern double FPDFText_GetFontSize(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern uint FPDFText_GetFontInfo(nint text,int index,byte[] buffer,uint length,out int flags);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetFontWeight(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetFillColor(nint text,int index,out uint r,out uint g,out uint b,out uint a);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetCharOrigin(nint text,int index,out double x,out double y);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetCharBox(nint text,int index,out double left,out double right,out double bottom,out double top);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFText_GetMatrix(nint text,int index,out Matrix matrix);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern nint FPDFText_GetTextObject(nint text,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFTextObj_GetTextRenderMode(nint item);
        [DllImport("pdfium",CallingConvention=CallingConvention.Winapi)]internal static extern int FPDFPageObj_SetIsActive(nint item,int active);
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
namespace Nashi.Core;

/// One run of uniformly styled text. Color is 0xAARRGGBB.
public sealed record SlideTextRun(string Text, string Font, double Size, bool Bold, bool Italic, uint Color, double Spacing = 0);
/// One positioned line of text in points from the page's top-left corner.
/// Ascent is where PowerPoint puts the first baseline below the box top, in em
/// (font dependent); 0 uses <see cref="PptxWriter.BaselineOffset"/>.
public sealed record SlideTextLine(double X, double Baseline, double Width, IReadOnlyList<SlideTextRun> Runs, double Ascent = 0)
{
    public double Size => Runs.Count == 0 ? 0 : Runs.Max(r => r.Size);
}
/// A PDF page split into a picture of everything that is not editable text
/// and the text drawn over it. Sizes are in points.
public sealed record SlidePage(double Width, double Height, byte[] Background, string BackgroundExtension, IReadOnlyList<SlideTextLine> Lines);

/// Writes a minimal, standards-conforming PresentationML package without
/// Office or LibreOffice. Every page becomes a slide whose background is the
/// rendered page and whose text lines are editable text boxes.
public static class PptxWriter
{
    private const double Emu = 12700; // EMU per point
    // PowerPoint puts the first baseline of a zero-inset box with 1.2 em
    // line pitch about 1.2 em minus the font's descent below the top; this
    // is the typical value (measured in PowerPoint) when the font is unknown.
    public static double BaselineOffset { get; set; } = .93;

    public static void Write(string output, IReadOnlyList<SlidePage> pages, string title, CancellationToken token = default)
    {
        if (pages.Count == 0) throw new InvalidDataException("This PDF contains no pages.");
        // PowerPoint accepts 1–56 inch slides. Larger or smaller pages are
        // scaled uniformly so geometry and font sizes keep their proportions.
        var first = pages[0];
        var longest = Math.Max(first.Width, first.Height); var shortest = Math.Min(first.Width, first.Height);
        if (!double.IsFinite(longest) || shortest <= 0) throw new InvalidDataException("This PDF page has no usable size.");
        var fit = longest > 4032 ? 4032 / longest : shortest < 72 ? Math.Min(72 / shortest, 4032 / longest) : 1;
        var slideWidth = (long)Math.Round(first.Width * fit * Emu); var slideHeight = (long)Math.Round(first.Height * fit * Emu);
        using var stream = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Text(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); writer.Write(content); }
        var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
        types.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/><Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/><Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        var presentationRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps\" Target=\"presProps.xml\"/><Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps\" Target=\"viewProps.xml\"/><Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles\" Target=\"tableStyles.xml\"/>");
        var slideIds = new StringBuilder();
        for (var n = 0; n < pages.Count; n++)
        {
            token.ThrowIfCancellationRequested();
            var page = pages[n]; var number = n + 1; var extension = page.BackgroundExtension == "png" ? "png" : "jpeg";
            types.Append($"<Override PartName=\"/ppt/slides/slide{number}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            presentationRels.Append($"<Relationship Id=\"rId{n + 10}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{number}.xml\"/>");
            slideIds.Append($"<p:sldId id=\"{256 + n}\" r:id=\"rId{n + 10}\"/>");
            using (var image = zip.CreateEntry($"ppt/media/page{number}.{extension}", CompressionLevel.NoCompression).Open()) image.Write(page.Background);
            Text($"ppt/slides/_rels/slide{number}.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/page{number}.{extension}\"/></Relationships>");
            Text($"ppt/slides/slide{number}.xml", Slide(page, slideWidth, slideHeight));
        }
        types.Append("</Types>"); presentationRels.Append("</Relationships>");
        Text("[Content_Types].xml", types.ToString());
        Text("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>");
        Text("docProps/core.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>" + Escape(title) + "</dc:title></cp:coreProperties>");
        Text("docProps/app.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Nashi</Application><Slides>" + pages.Count + "</Slides></Properties>");
        Text("ppt/_rels/presentation.xml.rels", presentationRels.ToString());
        Text("ppt/presentation.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>{slideIds}</p:sldIdLst><p:sldSz cx=\"{slideWidth}\" cy=\"{slideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr></p:defaultTextStyle></p:presentation>");
        Text("ppt/presProps.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:presentationPr xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>");
        Text("ppt/viewProps.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:viewPr xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        Text("ppt/tableStyles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>");
        Text("ppt/slideMasters/_rels/slideMaster1.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
        Text("ppt/slideMasters/slideMaster1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldMaster xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr></p:otherStyle></p:txStyles></p:sldMaster>");
        Text("ppt/slideLayouts/_rels/slideLayout1.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Text("ppt/slideLayouts/slideLayout1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldLayout xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Text("ppt/theme/theme1.xml", Theme);
    }

    private static string Slide(SlidePage page, long slideWidth, long slideHeight)
    {
        // Pages of a different size than the first are fitted and centered.
        var scale = Math.Min(slideWidth / (page.Width * Emu), slideHeight / (page.Height * Emu));
        var offsetX = (slideWidth - page.Width * Emu * scale) / 2; var offsetY = (slideHeight - page.Height * Emu * scale) / 2;
        long E(double points) => (long)Math.Round(points * Emu * scale);
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>");
        // The page picture is a locked shape at the back so text can be
        // edited without dragging the artwork, yet it can still be replaced.
        xml.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page artwork\" descr=\"Page artwork\"/><p:cNvPicPr><a:picLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\" noMove=\"1\" noResize=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"{(long)offsetX}\" y=\"{(long)offsetY}\"/><a:ext cx=\"{E(page.Width)}\" cy=\"{E(page.Height)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        var id = 3;
        foreach (var line in page.Lines)
        {
            if (line.Runs.Count == 0 || line.Runs.All(r => string.IsNullOrWhiteSpace(r.Text))) continue;
            var size = line.Size; var top = line.Baseline - size * (line.Ascent > 0 ? line.Ascent : BaselineOffset);
            var x = (long)(offsetX + line.X * Emu * scale); var y = (long)(offsetY + top * Emu * scale);
            var cx = Math.Max(E(line.Width + size * .1), 12700); var cy = Math.Max(E(size * 1.2), 12700);
            xml.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id - 2}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" rtlCol=\"0\" anchor=\"t\"><a:noAutofit/></a:bodyPr><a:lstStyle/><a:p><a:pPr><a:lnSpc><a:spcPts val=\"{(int)Math.Round(size * scale * 120)}\"/></a:lnSpc><a:spcBef><a:spcPts val=\"0\"/></a:spcBef><a:spcAft><a:spcPts val=\"0\"/></a:spcAft></a:pPr>");
            foreach (var run in line.Runs)
            {
                if (run.Text.Length == 0) continue;
                var sz = (int)Math.Clamp(Math.Round(run.Size * scale * 100), 100, 400000); var rgb = (run.Color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture); var alpha = (run.Color >> 24) & 0xFF;
                var fill = alpha >= 255 ? $"<a:srgbClr val=\"{rgb}\"/>" : $"<a:srgbClr val=\"{rgb}\"><a:alpha val=\"{alpha * 100000 / 255}\"/></a:srgbClr>";
                var font = Escape(run.Font);
                var spacing = (int)Math.Clamp(Math.Round(run.Spacing * scale * 100), -400000, 400000);
                xml.Append($"<a:r><a:rPr lang=\"en-US\" sz=\"{sz}\" b=\"{(run.Bold ? 1 : 0)}\" i=\"{(run.Italic ? 1 : 0)}\"{(spacing != 0 ? $" spc=\"{spacing}\"" : "")} dirty=\"0\"><a:solidFill>{fill}</a:solidFill><a:latin typeface=\"{font}\"/><a:ea typeface=\"{font}\"/><a:cs typeface=\"{font}\"/></a:rPr><a:t>{Escape(run.Text)}</a:t></a:r>");
            }
            xml.Append("</a:p></p:txBody></p:sp>"); id++;
        }
        xml.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return xml.ToString();
    }

    // XML 1.0 forbids most control characters; PDFs sometimes carry them.
    private static string Escape(string value)
    {
        var clean = new StringBuilder(value.Length);
        foreach (var c in value) if (c is '\t' or '\n' or '\r' || c >= ' ' && c != '￾' && c != '￿') clean.Append(c);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private const string Theme = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Nashi\"><a:themeElements><a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
}

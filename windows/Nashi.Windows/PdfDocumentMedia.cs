using System.Text;
using System.Net;
using Nashi.Core;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using global::Windows.Storage.Streams;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
namespace Nashi.Windows;
internal static class PdfDocumentMedia
{
    internal static bool OcrAvailable=>OcrEngine.TryCreateFromUserProfileLanguages() is not null||OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US")) is not null;
    internal static async Task<string> Recognize(byte[] image,CancellationToken token)
    {
        var engine=OcrEngine.TryCreateFromUserProfileLanguages()??OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))??throw new InvalidOperationException("Install an OCR language in Windows Language settings to recognize scanned PDF text.");token.ThrowIfCancellationRequested();var source=ImageEngine.FromPng(image);var factor=Math.Min(1,2400d/Math.Max(source.PixelWidth,source.PixelHeight));if(factor<1)source=new TransformedBitmap(source,new ScaleTransform(factor,factor));
        using var memory=new InMemoryRandomAccessStream();using(var writer=new DataWriter(memory.GetOutputStreamAt(0))){writer.WriteBytes(ImageEngine.PngBytes(source));await writer.StoreAsync();}memory.Seek(0);var decoder=await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(memory);using var bitmap=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);var result=await engine.RecognizeAsync(bitmap);token.ThrowIfCancellationRequested();return string.Join("\n",result.Lines.Select(l=>l.Text));
    }
    internal static async Task<string> Text(string input,string password,CancellationToken token)
    {
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(input,new(){Password=password});byte[][]? images=null;var text=new List<string>();foreach(var page in pdf.GetPages())
        {token.ThrowIfCancellationRequested();var content=UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);if(string.IsNullOrWhiteSpace(content)){images??=await NativePdf.RenderAsync(input,180,token,password);content=await Recognize(images[page.Number-1],token);}text.Add(content);}var output=string.Join("\n\n\f\n\n",text);if(string.IsNullOrWhiteSpace(output))throw new InvalidDataException("No text could be extracted or recognized in this PDF.");return output;
    }
}

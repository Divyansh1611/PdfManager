using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;


// use for screenshots
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace PdfManager.Services
{
    public class PdfService
    {
        private readonly IWebHostEnvironment _env;

        public PdfService(IWebHostEnvironment env)
        {
            _env = env;
        }

        // Parse page input like "1,3,5-8" → [1,3,5,6,7,8]
        public List<int> ParsePageNumbers(string input, int totalPages)
        {
            var pages = new HashSet<int>();

            foreach (var part in input.Split(','))
            {
                var trimmed = part.Trim();

                if (trimmed.Contains('-'))
                {
                    // Range like "5-8"
                    var range = trimmed.Split('-');
                    if (range.Length == 2 &&
                        int.TryParse(range[0], out int start) &&
                        int.TryParse(range[1], out int end))
                    {
                        for (int i = start; i <= end; i++)
                        {
                            if (i >= 1 && i <= totalPages)
                                pages.Add(i);
                        }
                    }
                }
                else
                {
                    // Single page like "3"
                    if (int.TryParse(trimmed, out int page))
                    {
                        if (page >= 1 && page <= totalPages)
                            pages.Add(page);
                    }
                }
            }

            return pages.OrderBy(p => p).ToList();
        }

        // Extract selected pages → return new PDF bytes
        public byte[] ExtractPages(string fileName, List<int> pageNumbers)
        {
            var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");
            var filePath = Path.Combine(uploadsFolder, fileName);

            using var inputDoc = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
            using var outputDoc = new PdfDocument();

            foreach (var pageNum in pageNumbers)
            {
                // PdfSharp is 0-indexed
                outputDoc.AddPage(inputDoc.Pages[pageNum - 1]);
            }

            using var ms = new MemoryStream();
            outputDoc.Save(ms, false);
            return ms.ToArray();
        }

        // Get total pages of a PDF
        public int GetTotalPages(string fileName)
        {
            var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");
            var filePath = Path.Combine(uploadsFolder, fileName);

            using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
            return doc.PageCount;
        }

        // Ye method PdfService class ke andar add karo
        public byte[] ConvertPageToImage(string fileName, int pageNumber, int dpi = 150)
        {
            var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");
            var filePath = Path.Combine(uploadsFolder, fileName);

            using var library = DocLib.Instance;

            // ✅ Bada size do taaki poora page render ho
            int width = (int)(8.27 * dpi);
            int height = (int)(11.69 * dpi);

            using var docReader = library.GetDocReader(
                filePath,
                new PageDimensions(width, height));

            using var pageReader = docReader.GetPageReader(pageNumber - 1);

            var actualWidth = pageReader.GetPageWidth();
            var actualHeight = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();

            // ✅ Fix: BGRA → RGBA color correction
            var correctedBytes = new byte[rawBytes.Length];
            for (int i = 0; i < rawBytes.Length; i += 4)
            {
                correctedBytes[i] = rawBytes[i + 2]; // R
                correctedBytes[i + 1] = rawBytes[i + 1]; // G
                correctedBytes[i + 2] = rawBytes[i];     // B
                correctedBytes[i + 3] = 255;             // Alpha = fully opaque
            }

            using var bitmap = new SKBitmap();
            var gcHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                correctedBytes,
                System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                var info = new SKImageInfo(
                    actualWidth,
                    actualHeight,
                    SKColorType.Rgba8888,
                    SKAlphaType.Unpremul);

                bitmap.InstallPixels(
                    info,
                    gcHandle.AddrOfPinnedObject(),
                    info.RowBytes);

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
            finally
            {
                gcHandle.Free();
            }
        }

        // Multiple pages → ZIP file
        public byte[] ConvertPagesToZip(string fileName, List<int> pageNumbers)
        {
            using var ms = new MemoryStream();
            using var archive = new System.IO.Compression.ZipArchive(
                ms, System.IO.Compression.ZipArchiveMode.Create, true);

            foreach (var pageNum in pageNumbers)
            {
                var imgBytes = ConvertPageToImage(fileName, pageNum);
                var entry = archive.CreateEntry($"page_{pageNum}.png",
                    System.IO.Compression.CompressionLevel.Fastest);

                using var entryStream = entry.Open();
                entryStream.Write(imgBytes, 0, imgBytes.Length);
            }

            ms.Position = 0;
            return ms.ToArray();
        }

    }

}
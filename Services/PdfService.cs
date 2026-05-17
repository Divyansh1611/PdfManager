using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace PdfManager.Services
{
    public class PdfService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private readonly BlobContainerClient _containerClient;

        public PdfService(IWebHostEnvironment env, IConfiguration configuration)
        {
            _env = env;
            _configuration = configuration;

            var connectionString = configuration["AzureStorage__ConnectionString"]
                ?? configuration["AzureStorage:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Azure Storage connection string not found!");

            var containerName = configuration["AzureStorage:ContainerName"] ?? "uploads";

            _containerClient = new BlobContainerClient(connectionString, containerName);
            _containerClient.CreateIfNotExists(PublicAccessType.Blob);
        }

        // ─────────────────────────────────────
        // Upload PDF to Azure Blob Storage
        // ─────────────────────────────────────
        public async Task<string> UploadToBlob(IFormFile file)
        {
            var uniqueFileName = Guid.NewGuid().ToString() + "_" + file.FileName;
            var blobClient = _containerClient.GetBlobClient(uniqueFileName);

            using var stream = file.OpenReadStream();
            await blobClient.UploadAsync(stream, new BlobHttpHeaders
            {
                ContentType = "application/pdf"
            });

            return uniqueFileName;
        }

        // ─────────────────────────────────────
        // Download PDF from Blob to temp path
        // ─────────────────────────────────────
        public async Task<string> DownloadToTemp(string fileName)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);

            if (!File.Exists(tempPath))
            {
                var blobClient = _containerClient.GetBlobClient(fileName);
                await blobClient.DownloadToAsync(tempPath);
            }

            return tempPath;
        }

        // ─────────────────────────────────────
        // Delete PDF from Blob
        // ─────────────────────────────────────
        public async Task DeleteFromBlob(string fileName)
        {
            var blobClient = _containerClient.GetBlobClient(fileName);
            await blobClient.DeleteIfExistsAsync();
        }

        // ─────────────────────────────────────
        // Get Blob URL for PDF viewer
        // ─────────────────────────────────────
        public string GetBlobUrl(string fileName)
        {
            var blobClient = _containerClient.GetBlobClient(fileName);
            return blobClient.Uri.ToString();
        }

        // ─────────────────────────────────────
        // Parse page numbers "1,3,5-8"
        // ─────────────────────────────────────
        public List<int> ParsePageNumbers(string input, int totalPages)
        {
            var pages = new HashSet<int>();

            foreach (var part in input.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var range = trimmed.Split('-');
                    if (range.Length == 2 &&
                        int.TryParse(range[0], out int start) &&
                        int.TryParse(range[1], out int end))
                    {
                        for (int i = start; i <= end; i++)
                            if (i >= 1 && i <= totalPages) pages.Add(i);
                    }
                }
                else
                {
                    if (int.TryParse(trimmed, out int page))
                        if (page >= 1 && page <= totalPages) pages.Add(page);
                }
            }

            return pages.OrderBy(p => p).ToList();
        }

        // ─────────────────────────────────────
        // Extract Pages → New PDF bytes
        // ─────────────────────────────────────
        public async Task<byte[]> ExtractPages(string fileName, List<int> pageNumbers)
        {
            var tempPath = await DownloadToTemp(fileName);

            using var inputDoc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Import);
            using var outputDoc = new PdfDocument();

            foreach (var pageNum in pageNumbers)
                outputDoc.AddPage(inputDoc.Pages[pageNum - 1]);

            using var ms = new MemoryStream();
            outputDoc.Save(ms, false);
            return ms.ToArray();
        }

        // ─────────────────────────────────────
        // Get Total Pages
        // ─────────────────────────────────────
        public async Task<int> GetTotalPages(string fileName)
        {
            var tempPath = await DownloadToTemp(fileName);
            using var doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.InformationOnly);
            return doc.PageCount;
        }

        // ─────────────────────────────────────
        // Convert Page to Image (PNG)
        // ─────────────────────────────────────
        public async Task<byte[]> ConvertPageToImage(string fileName,
            int pageNumber, int dpi = 150)
        {
            var tempPath = await DownloadToTemp(fileName);

            using var library = DocLib.Instance;
            int width = (int)(8.27 * dpi);
            int height = (int)(11.69 * dpi);

            using var docReader = library.GetDocReader(
                tempPath, new PageDimensions(width, height));
            using var pageReader = docReader.GetPageReader(pageNumber - 1);

            var actualWidth = pageReader.GetPageWidth();
            var actualHeight = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();

            var correctedBytes = new byte[rawBytes.Length];
            for (int i = 0; i < rawBytes.Length; i += 4)
            {
                correctedBytes[i] = rawBytes[i + 2];
                correctedBytes[i + 1] = rawBytes[i + 1];
                correctedBytes[i + 2] = rawBytes[i];
                correctedBytes[i + 3] = 255;
            }

            using var bitmap = new SKBitmap();
            var gcHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                correctedBytes,
                System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                var info = new SKImageInfo(actualWidth, actualHeight,
                    SKColorType.Rgba8888, SKAlphaType.Unpremul);

                bitmap.InstallPixels(info, gcHandle.AddrOfPinnedObject(),
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

        // ─────────────────────────────────────
        // Convert Multiple Pages → ZIP
        // ─────────────────────────────────────
        public async Task<byte[]> ConvertPagesToZip(string fileName,
            List<int> pageNumbers)
        {
            using var ms = new MemoryStream();
            using var archive = new System.IO.Compression.ZipArchive(
                ms, System.IO.Compression.ZipArchiveMode.Create, true);

            foreach (var pageNum in pageNumbers)
            {
                var imgBytes = await ConvertPageToImage(fileName, pageNum);
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
using Microsoft.AspNetCore.Mvc;
using PdfManager.Models;
using PdfManager.Services;

namespace PdfManager.Controllers
{
    public class PdfController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly PdfService _pdfService;

        public PdfController(IWebHostEnvironment env, PdfService pdfService)
        {
            _env = env;
            _pdfService = pdfService;
        }

        // GET - Upload Page
        public IActionResult Index()
        {
            return View();
        }

        // POST - Handle Upload → Azure Blob
        [HttpPost]
        public async Task<IActionResult> Upload(PdfUploadModel model)
        {
            if (model.PdfFile == null || model.PdfFile.Length == 0)
            {
                TempData["Error"] = "Please select a PDF file!";
                return RedirectToAction("Index");
            }

            if (!model.PdfFile.FileName.EndsWith(".pdf",
                StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only PDF files are allowed!";
                return RedirectToAction("Index");
            }

            if (model.PdfFile.Length > 50 * 1024 * 1024)
            {
                TempData["Error"] = "File size must be less than 50MB!";
                return RedirectToAction("Index");
            }

            // ✅ Azure Blob mein upload karo
            var uniqueFileName = await _pdfService.UploadToBlob(model.PdfFile);

            HttpContext.Session.SetString("UploadedPdf", uniqueFileName);
            TempData["Success"] = "PDF uploaded successfully!";

            return RedirectToAction("Viewer");
        }

        // GET - PDF Viewer
        public async Task<IActionResult> Viewer()
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (string.IsNullOrEmpty(fileName))
                return RedirectToAction("Index");

            // ✅ Blob URL pass karo viewer ko
            ViewBag.FileName = fileName;
            ViewBag.PdfUrl = _pdfService.GetBlobUrl(fileName);
            return View();
        }

        // GET - Extract Page UI
        public async Task<IActionResult> Extract()
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (string.IsNullOrEmpty(fileName))
                return RedirectToAction("Index");

            var totalPages = await _pdfService.GetTotalPages(fileName);
            ViewBag.FileName = fileName;
            ViewBag.TotalPages = totalPages;

            return View();
        }

        // POST - Extract Pages
        [HttpPost]
        public async Task<IActionResult> DoExtract(string pageInput)
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (string.IsNullOrEmpty(fileName))
                return RedirectToAction("Index");

            var totalPages = await _pdfService.GetTotalPages(fileName);
            var pageNumbers = _pdfService.ParsePageNumbers(pageInput, totalPages);

            if (pageNumbers.Count == 0)
            {
                TempData["Error"] = "Invalid page numbers entered!";
                return RedirectToAction("Extract");
            }

            var pdfBytes = await _pdfService.ExtractPages(fileName, pageNumbers);
            return File(pdfBytes, "application/pdf",
                $"extracted_pages_{string.Join("-", pageNumbers)}.pdf");
        }

        // GET - Screenshot Single Page
        public async Task<IActionResult> Screenshot(int pageNum)
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (string.IsNullOrEmpty(fileName))
                return RedirectToAction("Index");

            var imgBytes = await _pdfService.ConvertPageToImage(fileName, pageNum);
            return File(imgBytes, "image/png", $"page_{pageNum}.png");
        }

        // POST - Screenshot Multiple Pages
        [HttpPost]
        public async Task<IActionResult> ScreenshotMultiple(string pageInput)
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (string.IsNullOrEmpty(fileName))
                return RedirectToAction("Index");

            var totalPages = await _pdfService.GetTotalPages(fileName);
            var pageNumbers = _pdfService.ParsePageNumbers(pageInput, totalPages);

            if (pageNumbers.Count == 0)
            {
                TempData["Error"] = "Invalid page numbers!";
                return RedirectToAction("Extract");
            }

            if (pageNumbers.Count == 1)
            {
                var imgBytes = await _pdfService.ConvertPageToImage(
                    fileName, pageNumbers[0]);
                return File(imgBytes, "image/png",
                    $"page_{pageNumbers[0]}.png");
            }

            var zipBytes = await _pdfService.ConvertPagesToZip(fileName, pageNumbers);
            return File(zipBytes, "application/zip",
                $"pages_{string.Join("-", pageNumbers)}.zip");
        }

        // GET - Delete PDF
        public async Task<IActionResult> Delete()
        {
            var fileName = HttpContext.Session.GetString("UploadedPdf");
            if (!string.IsNullOrEmpty(fileName))
            {
                // ✅ Blob se delete karo
                await _pdfService.DeleteFromBlob(fileName);
                HttpContext.Session.Remove("UploadedPdf");
            }

            TempData["Success"] = "PDF deleted successfully!";
            return RedirectToAction("Index");
        }
    }
}
using System.ComponentModel.DataAnnotations;

namespace PdfManager.Models
{
    public class PdfUploadModel
    {
        [Required(ErrorMessage = "Please select a PDF file")]
        public IFormFile PdfFile { get; set; }
    }
}

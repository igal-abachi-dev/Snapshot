using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Snapshot
{
    /*
    
    it removes all whitespace (making it immune to email client line wrapping/spacing changes).

    Solves Data Corruption: Base64 uses only email-safe ASCII characters. Email systems will not corrupt the Base64 encoded data itself. This is the standard way to safely represent binary data in text-based environments like email.

    Handles Line Wrapping: The import logic stripping whitespace makes it robust against email clients reformatting the Base64 block.
    */
    /// <summary>
    /// Provides email-specific export/import functionality for TLV snapshots.
    /// </summary>
    public static class EmailExport
    {
        /// <summary>
        /// Exports the repository as a base64-encoded string suitable for email body inclusion.
        /// </summary>
        public static string ExportAsBase64Email(string repoPath)
        {
            // Create temporary snapshot file
            string tempFile = Path.GetTempFileName();
            try
            {
                // Export to temp file
                TLVSnapshot.Export(repoPath, tempFile, false, null);
               
                // Create output with markers
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("-----BEGIN TLV SNAPSHOT-----");
               
                // Read and encode in chunks to avoid excessive memory usage
                using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = new byte[3 * 16 * 1024]; // Multiple of 3 for clean base64
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        string chunk = Convert.ToBase64String(buffer, 0, bytesRead);
                        // Add line breaks for email readability, 76 chars per line
                        for (int i = 0; i < chunk.Length; i += 76)
                        {
                            int len = Math.Min(76, chunk.Length - i);
                            sb.AppendLine(chunk.Substring(i, len));
                        }
                    }
                }
               
                sb.AppendLine("-----END TLV SNAPSHOT-----");
                return sb.ToString();
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        /// <summary>
        /// Imports a repository from base64-encoded email content.
        /// </summary>
        public static void ImportFromBase64Email(string emailContent, string destPath)
        {
            const string beginMarker = "-----BEGIN TLV SNAPSHOT-----";
            const string endMarker = "-----END TLV SNAPSHOT-----";
           
            int startPos = emailContent.IndexOf(beginMarker);
            int endPos = emailContent.IndexOf(endMarker);
           
            if (startPos < 0 || endPos <= startPos)
                throw new FormatException("Invalid email format: missing begin/end markers");
           
            // Extract content between markers
            string base64Content = emailContent.Substring(
                startPos + beginMarker.Length,
                endPos - startPos - beginMarker.Length
            );
           
            // Remove all whitespace from base64 content
            base64Content = new string(base64Content.Where(c => !char.IsWhiteSpace(c)).ToArray());
           
            // Decode to binary and import
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempFile, Convert.FromBase64String(base64Content));
                TLVSnapshot.Import(tempFile, destPath);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}

namespace Marble
{
    public class Program
    {
        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("Extract MBL:     MblTool -e <archive_file> <output_dir>");
            Console.WriteLine("Pack MBL:        MblTool -p <input_dir> <output_archive> [version]");
            Console.WriteLine("Convert PRS To Png:     MblTool -i <input_dir> <output_dir>");
            Console.WriteLine("Convert PNG To Prs:     MblTool -cp <input_dir> <output_dir>");
        }

        static void Main(string[] args)
        {
            // Register encoding provider for Shift-JIS support
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            try
            {
                string selectedKey = null;
                if (args[0].ToLower() == "-e")
                {
                    if (args[1].Contains("_data"))
                    {
                        var GameKeys = new GameKeysManager();
                        selectedKey = GameKeys.PromptForKey();
                    }
                    else
                    {
                        selectedKey = ""; // Default key, no prompt if _data isn't present
                    }
                }

                switch (args[0].ToLower())
                {
                    case "-e":
                        if (args.Length != 3)
                        {
                            PrintUsage();
                            return;
                        }
                        ExtractArchive(args[1], args[2], selectedKey);
                        break;

                    case "-p":
                        if (args.Length < 3 || args.Length > 4)
                        {
                            PrintUsage();
                            return;
                        }
                        PackArchive(args[1], args[2]);
                        break;

                    case "-i":
                        if (args.Length != 3)
                        {
                            PrintUsage();
                            return;
                        }
                        PrsToPngConverter.ConvertPrsToPng(args[1], args[2]);
                        break;

                    case "-cp":
                        if (args.Length != 3)
                        {
                            PrintUsage();
                            return;
                        }
                        PngToPrsConverter.ConvertPngToPrs(args[1], args[2]);
                        break;

                    default:
                        PrintUsage();
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
                if (e.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {e.InnerException.Message}");
                }
            }
        }

        static void ExtractArchive(string archiveFile, string outputDir, string key)
        {
            if (!File.Exists(archiveFile))
                throw new FileNotFoundException("Archive file not found", archiveFile);

            Console.WriteLine($"Extracting {archiveFile} to {outputDir}");
            var opener = new MblOpener(archiveFile, key);
            if (!opener.Extract())
            {
                throw new InvalidOperationException("Failed to extract archive");
            }
            opener.SaveFiles(outputDir);
            Console.WriteLine("Extraction completed successfully");
        }

        static void PackArchive(string inputDir, string outputArchive)
        {
            if (!Directory.Exists(inputDir))
                throw new DirectoryNotFoundException("Input directory not found");
            
            var packer = new MblPacker(outputArchive);
            packer.Pack(inputDir);
            Console.WriteLine("Packing completed successfully");
        }
        
    }
}

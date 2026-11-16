using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Beachtro.Application.Receipts;

namespace Beachtro.Desktop.Printing;

/// <summary>
/// Windows printer adapter (D-16): sends the fixed-width receipt as a monospace document to an
/// installed queue through the driver. Spool acceptance is reported; whether paper came out is
/// confirmed by the operator. Works with virtual printers such as "Microsoft Print to PDF".
/// </summary>
public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
    public IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (PrintSystemException)
        {
            return [];
        }
    }

    public Task<PrintSubmission> PrintAsync(ReceiptDocument document, string printerName, CancellationToken cancellationToken = default) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Print(document, printerName)).Task;

    private static PrintSubmission Print(ReceiptDocument document, string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            var queue = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .FirstOrDefault(q => string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase));
            if (queue is null)
            {
                return new PrintSubmission(false, null, $"Printer '{printerName}' is not installed.");
            }

            var dialog = new PrintDialog { PrintQueue = queue };
            var width = Math.Min(dialog.PrintableAreaWidth, 302); // about 80 mm at 96 dpi
            var flow = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                PagePadding = new Thickness(4),
                PageWidth = width,
                ColumnWidth = width,
            };
            foreach (var line in document.Lines)
            {
                flow.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0), LineHeight = 11 });
            }

            dialog.PrintDocument(((IDocumentPaginatorSource)flow).DocumentPaginator, $"Beachtro {document.SourceType} {document.Number}");
            return new PrintSubmission(true, null, null);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new PrintSubmission(false, null, "The printer rejected the job: " + ex.GetType().Name);
        }
    }
}

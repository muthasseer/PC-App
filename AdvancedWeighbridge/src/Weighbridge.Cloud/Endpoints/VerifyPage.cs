using System.Globalization;
using System.Text.Encodings.Web;
using Weighbridge.Contracts;
using Weighbridge.Core.Units;

namespace Weighbridge.Cloud.Endpoints;

/// <summary>Small server-rendered page opened by phones that scan a ticket QR code. All values are HTML-encoded.</summary>
public static class VerifyPage
{
    public static string Render(TicketVerifyDto r)
    {
        static string E(string? s) => HtmlEncoder.Default.Encode(s ?? string.Empty);
        var rows = new List<(string, string?)>
        {
            ("Ticket", r.TicketNo),
            ("Site", r.SiteCode),
            ("Status", r.Status),
            ("Vehicle", r.VehicleReg),
            ("Net weight", r.NetG is { } n ? WeightDisplayOptions.DefaultKg.Format(Weight.FromGrams(n)) : null),
            ("Completed (UTC)", r.CompletedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };
        var details = string.Concat(rows.Where(x => !string.IsNullOrEmpty(x.Item2)).Select(x => $"<tr><th>{E(x.Item1)}</th><td>{E(x.Item2)}</td></tr>"));
        var state = r.Valid ? "ok" : "bad";
        var symbol = r.Valid ? "✔" : "✖";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Ticket check – AWMS</title>
              <link rel="stylesheet" href="/app.css">
            </head>
            <body class="verify">
              <main class="verify-card {{state}}">
                <p class="verify-symbol" aria-hidden="true">{{symbol}}</p>
                <h1>{{(r.Valid ? "Ticket matches the record" : "Ticket could not be confirmed")}}</h1>
                <p>{{E(r.Message)}}</p>
                <table>{{details}}</table>
                <p class="muted">This page compares the ticket with the copy synchronised to the AWMS cloud. The digital signature on the ticket is verified at the issuing site.</p>
              </main>
            </body>
            </html>
            """;
    }
}

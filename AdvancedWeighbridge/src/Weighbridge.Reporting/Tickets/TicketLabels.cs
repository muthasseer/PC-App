namespace Weighbridge.Reporting.Tickets;

/// <summary>
/// Ticket wording in English, Sinhala and Tamil. The Sinhala and Tamil texts must be reviewed by a native speaker
/// before production use (see docs/localisation.md); they can be overridden per installation.
/// </summary>
public static class TicketLabels
{
    private static readonly Dictionary<string, (string En, string Si, string Ta)> Texts = new(StringComparer.Ordinal)
    {
        ["title"] = ("WEIGHMENT TICKET", "කිරුම් පත්‍රය", "எடை சீட்டு"),
        ["ticketNo"] = ("Ticket No", "පත්‍ර අංකය", "சீட்டு எண்"),
        ["date"] = ("Date", "දිනය", "தேதி"),
        ["vehicle"] = ("Vehicle No", "වාහන අංකය", "வாகன எண்"),
        ["driver"] = ("Driver", "රියදුරු", "ஓட்டுநர்"),
        ["transporter"] = ("Transporter", "ප්‍රවාහකයා", "போக்குவரத்து நிறுவனம்"),
        ["customer"] = ("Customer", "ගනුදෙනුකරු", "வாடிக்கையாளர்"),
        ["supplier"] = ("Supplier", "සැපයුම්කරු", "வழங்குநர்"),
        ["material"] = ("Material", "ද්‍රව්‍යය", "பொருள்"),
        ["orderNo"] = ("Order No", "ඇණවුම් අංකය", "ஆணை எண்"),
        ["deliveryNote"] = ("Delivery note", "බෙදාහැරීම් සටහන", "விநியோகக் குறிப்பு"),
        ["reference"] = ("Reference", "යොමු අංකය", "குறிப்பு எண்"),
        ["direction"] = ("Direction", "දිශාව", "திசை"),
        ["weighbridge"] = ("Weighbridge", "කිරුම් පාලම", "எடைப் பாலம்"),
        ["mode"] = ("Mode", "ක්‍රමය", "முறை"),
        ["first"] = ("First weight", "පළමු බර", "முதல் எடை"),
        ["second"] = ("Second weight", "දෙවන බර", "இரண்டாம் எடை"),
        ["gross"] = ("GROSS", "දළ බර", "மொத்த எடை"),
        ["tare"] = ("TARE", "හිස් බර", "வெற்று எடை"),
        ["net"] = ("NET", "ශුද්ධ බර", "நிகர எடை"),
        ["weight"] = ("Weight", "බර", "எடை"),
        ["time"] = ("Date & time", "දිනය සහ වේලාව", "தேதி மற்றும் நேரம்"),
        ["source"] = ("Source", "මූලාශ්‍රය", "மூலம்"),
        ["tareMethod"] = ("Tare method", "හිස් බර ක්‍රමය", "வெற்று எடை முறை"),
        ["operator"] = ("Operator", "ක්‍රියාකරු", "இயக்குநர்"),
        ["remarks"] = ("Remarks", "සටහන්", "குறிப்புகள்"),
        ["driverSignature"] = ("Driver signature", "රියදුරුගේ අත්සන", "ஓட்டுநர் கையொப்பம்"),
        ["operatorSignature"] = ("Operator signature", "ක්‍රියාකරුගේ අත්සන", "இயக்குநர் கையொப்பம்"),
        ["authorizedSignature"] = ("Authorised signature", "බලයලත් අත්සන", "அங்கீகரிக்கப்பட்ட கையொப்பம்"),
        ["printed"] = ("Printed", "මුද්‍රණය කළේ", "அச்சிடப்பட்டது"),
        ["reprint"] = ("REPRINT", "නැවත මුද්‍රණය", "மறு அச்சு"),
        ["pending"] = ("FIRST WEIGHT – AWAITING SECOND WEIGHING", "පළමු බර – දෙවන කිරුම බලාපොරොත්තුවෙන්", "முதல் எடை – இரண்டாம் எடைக்காக காத்திருக்கிறது"),
        ["cancelled"] = ("CANCELLED", "අවලංගු කර ඇත", "ரத்து செய்யப்பட்டது"),
        ["manual"] = ("MANUAL ENTRY – NOT A VERIFIED INDICATOR MEASUREMENT", "අතින් ඇතුළත් කළ බර – දර්ශකයෙන් තහවුරු නොකළ මිනුමකි", "கைமுறை உள்ளீடு – காட்டியால் சரிபார்க்கப்படாத அளவீடு"),
        ["override"] = ("Unstable weight accepted by supervisor override", "අධීක්ෂක අනුමැතියෙන් අස්ථාවර බර පිළිගත්තා", "மேற்பார்வையாளர் அனுமதியுடன் நிலையற்ற எடை ஏற்கப்பட்டது"),
        ["verify"] = ("Scan to verify", "තහවුරු කිරීමට ස්කෑන් කරන්න", "சரிபார்க்க ஸ்கேன் செய்யவும்"),
        ["page"] = ("Page", "පිටුව", "பக்கம்"),
        ["of"] = ("of", "න්", "இல்"),
        ["total"] = ("Total", "එකතුව", "மொத்தம்"),
    };

    public static IReadOnlyCollection<string> Keys => Texts.Keys;

    public static string Get(string key, string language)
    {
        if (!Texts.TryGetValue(key, out var t)) return key;
        return language switch
        {
            "si" => t.Si,
            "ta" => t.Ta,
            _ => t.En,
        };
    }

    /// <summary>Primary label and, on bilingual tickets, the local-language label to show beneath or beside it.</summary>
    public static (string Primary, string? Secondary) Pair(string key, TicketTemplate template)
    {
        if (template.Language == "en") return (Get(key, "en"), null);
        if (!template.Bilingual) return (Get(key, template.Language), null);
        var local = Get(key, template.Language);
        var en = Get(key, "en");
        return (en, local == en ? null : local);
    }

    /// <summary>Single-line form: "Vehicle No · වාහන අංකය".</summary>
    public static string Inline(string key, TicketTemplate template)
    {
        var (primary, secondary) = Pair(key, template);
        return secondary is null ? primary : $"{primary} · {secondary}";
    }

    /// <summary>"Net / ශුද්ධ බර" style label for bilingual tickets.</summary>
    public static string Label(string key, TicketTemplate template)
    {
        var local = Get(key, template.Language);
        if (template.Language == "en" || !template.Bilingual) return local;
        var en = Get(key, "en");
        return en == local ? en : $"{en} / {local}";
    }
}

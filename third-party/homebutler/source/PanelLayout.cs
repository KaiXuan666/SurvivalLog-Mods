using System;
using System.IO;
using System.Text;

public static class PanelLayout
{
    public static readonly string[] Pages = { "BackpackUI", "Cooking", "ToolTable", "BrewPanel", "Heater", "FuelGenerator", "RatCage", "Compressor", "Shredder", "TradeUI" };
    const string Marker = "homebutler-container-ui-v120";
    public static string Transform(string text, string page, string script)
    {
        if(text.Contains(Marker)) return text;
        if(text.IndexOf("</body>",StringComparison.Ordinal)!=text.LastIndexOf("</body>",StringComparison.Ordinal) || !text.Contains("</body>") || !text.Contains("createWebUICore")) throw new InvalidOperationException("Unsupported panel: "+page);
        return text.Replace("</body>","<script id=\""+Marker+"\">\nwindow.HOME_PAGE = '"+page+"';\n"+script+"\n</script>\n</body>");
    }
    public static string Apply(string root,string page,string script,bool checkOnly)
    {
        var relative="SurvivalLog_Data/StreamingAssets/WebUI/UI/"+page+"/"+page+".html";
        var target=AdaptiveLayout.Safe(root,relative);
        var bytes=File.ReadAllBytes(target); var utf8=new UTF8Encoding(false,true); var text=utf8.GetString(bytes);
        var result=Transform(text,page,script);
        if(result==text || checkOnly) return page+": ready";
        var after=utf8.GetBytes(result);
        var backup=AdaptiveLayout.Safe(root,"BepInEx/HomeButler-panel-backups/"+page+"-"+AdaptiveLayout.Hash(after)+".original");
        if(File.Exists(backup) && AdaptiveLayout.Hash(File.ReadAllBytes(backup))!=AdaptiveLayout.Hash(bytes)) throw new IOException("Panel backup conflict");
        AdaptiveLayout.AtomicWrite(backup,bytes);
        AdaptiveLayout.AtomicWrite(backup+".sha256",Encoding.ASCII.GetBytes(AdaptiveLayout.Hash(bytes)));
        if(AdaptiveLayout.Hash(File.ReadAllBytes(target))!=AdaptiveLayout.Hash(bytes)) throw new IOException("Panel changed while patching");
        AdaptiveLayout.AtomicWrite(target,after); return page+": container controls installed";
    }
    public static void Restore(string root,string page,bool checkOnly)
    {
        var target=AdaptiveLayout.Safe(root,"SurvivalLog_Data/StreamingAssets/WebUI/UI/"+page+"/"+page+".html");
        var bytes=File.ReadAllBytes(target);
        if(!Encoding.UTF8.GetString(bytes).Contains(Marker)) return;
        var backup=AdaptiveLayout.Safe(root,"BepInEx/HomeButler-panel-backups/"+page+"-"+AdaptiveLayout.Hash(bytes)+".original");
        var original=File.ReadAllBytes(backup);
        if(AdaptiveLayout.Hash(original)!=File.ReadAllText(backup+".sha256")) throw new IOException("Panel backup damaged");
        if(!checkOnly) AdaptiveLayout.AtomicWrite(target,original);
    }
}

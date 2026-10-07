using System.Xml.Linq;
using System.Text;
using EncodeBridge.Domain;

namespace EncodeBridge.Adapters;

internal sealed class AmatsukazeAdapter : EncodeAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.Amatsukaze;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo s)
    {
        var profile = Required(item,"profile","Amatsukazeのプロファイルが設定されていません。");
        var ip = item.Profile.AdapterSettings.TryGetValue("serverIp", out var v) && !string.IsNullOrWhiteSpace(v) ? v : "localhost";
        var port = item.Profile.AdapterSettings.TryGetValue("serverPort", out var p) && !string.IsNullOrWhiteSpace(p) ? p : "32768";
        var outDir = Path.GetDirectoryName(Output(item)) ?? item.Profile.OutputDirectory;
        var args = new List<string>{"-f",item.Recording.SourceFilePath,"-s",profile,"-o",outDir,"-ip",ip,"-p",port};
        args.AddRange(item.Profile.AdditionalArguments);
        return new(item.Profile.ExecutablePath,args,Path.GetDirectoryName(item.Profile.ExecutablePath),Output(item),true);
    }
    private static string Required(EncodeQueueItem item,string key,string message)
        => item.Profile.AdapterSettings.TryGetValue(key,out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new InvalidOperationException(message);
}

internal sealed class TmpgEncAdapter : EncodeAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.TmpgEnc;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo s)
    {
        if (!item.Profile.AdapterSettings.TryGetValue("encodeTemplate",out var template) || string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            throw new InvalidOperationException("TMPGEncの出力テンプレートを使用できません。");
        var filter = item.Profile.AdapterSettings.TryGetValue("filterTemplate",out var f) ? f : string.Empty;
        var dir = Path.Combine(Path.GetTempPath(),"EncodeBridge"); Directory.CreateDirectory(dir);
        var xmlPath = Path.Combine(dir,$"{item.QueueItemId}-{item.AttemptNumber}.xml");
        var xml = BuildXml(item,template,filter);
        File.WriteAllText(xmlPath,xml,new UTF8Encoding(false));
        item.PreparedArtifactPath = xmlPath;
        var logPath = xmlPath + ".log";
        var args = new List<string>{"-XML",xmlPath,"-Log",logPath,"-LogLevel","I"};
        args.AddRange(item.Profile.AdditionalArguments);
        return new(item.Profile.ExecutablePath,args,Path.GetDirectoryName(item.Profile.ExecutablePath),Output(item),true,xmlPath);
    }
    private static string BuildXml(EncodeQueueItem item,string encodeTemplate,string filterTemplate)
    {
        var clip = new XElement("Clip",
            new XAttribute("clipname", item.Recording.Title),
            new XElement("SourceFileName", new XCData(item.Recording.SourceFilePath)));

        if (!string.IsNullOrWhiteSpace(filterTemplate))
            clip.Add(new XElement("FilterTemplateFileName", new XCData(filterTemplate)));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("TMPGEncVMWImportData",
                new XAttribute("version", "8.0.0"),
                new XElement("Task",
                    new XElement("General",
                        new XElement("BatchRegist", 1),
                        new XElement("BatchStart", 1)),
                    new XElement("InputSetting",
                        new XAttribute("inputmode", "0"),
                        clip),
                    new XElement("OutputSetting",
                        new XAttribute("outputmode", "0"),
                        new XElement("OutputTarget",
                            new XAttribute("target", "file"),
                            new XCData(Output(item))),
                        new XElement("EncodeTemplateFileName", new XCData(encodeTemplate)),
                        new XElement("MetaData",
                            new XElement("Title", new XCData(item.Recording.Title)))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }
}

internal sealed class CustomCliAdapter : EncodeAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.Custom;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo s)
    {
        if (!item.Profile.AdapterSettings.TryGetValue("arguments",out var raw)) raw = string.Empty;
        var args = CommandLineTemplate.Expand(raw,item.Recording.SourceFilePath,Output(item),item.Recording.Title,item.Recording.ServiceName).Concat(item.Profile.AdditionalArguments).ToArray();
        var handoff = item.Profile.AdapterSettings.TryGetValue("handoffOnly",out var h) && bool.TryParse(h,out var b) && b;
        return new(item.Profile.ExecutablePath,args,Path.GetDirectoryName(item.Profile.ExecutablePath),Output(item),handoff);
    }
}

internal static class CommandLineTemplate
{
    public static IReadOnlyList<string> Expand(string template,string input,string output,string title,string service)
    {
        var expanded = template.Replace("{input}",input,StringComparison.OrdinalIgnoreCase).Replace("{output}",output,StringComparison.OrdinalIgnoreCase).Replace("{title}",title,StringComparison.OrdinalIgnoreCase).Replace("{service}",service,StringComparison.OrdinalIgnoreCase);
        var result = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i=0;i<expanded.Length;i++)
        {
            var c=expanded[i];
            if(c=='\"'){ quoted=!quoted; continue; }
            if(char.IsWhiteSpace(c)&&!quoted){ if(sb.Length>0){result.Add(sb.ToString());sb.Clear();} continue; }
            sb.Append(c);
        }
        if(sb.Length>0) result.Add(sb.ToString());
        return result;
    }
}

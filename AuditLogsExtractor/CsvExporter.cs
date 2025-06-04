using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;

public class CsvExporter
{
    private readonly IOrganizationService _service;
    public readonly string _outputFolder;

    public CsvExporter(IOrganizationService service, string outputFolder = "output")
    {
        _service = service;
        _outputFolder = outputFolder;

        if (!Directory.Exists(_outputFolder))
        {
            Directory.CreateDirectory(_outputFolder);
        }
    }

    public string ExportarGrupoComoCsv(string entidad, Guid registroId, List<Entity> auditoria)
    {
        string nombreArchivo = Path.Combine(_outputFolder, $"{registroId}.csv");

        var auditoriaOrdenada = auditoria
            .Where(r => r.Attributes.Contains("createdon"))
            .OrderBy(r => r.GetAttributeValue<DateTime>("createdon"))
            .ToList();

        using (var writer = new StreamWriter(nombreArchivo, false, Encoding.UTF8))
        {
            writer.WriteLine("AuditID,Fecha,Entidad,RegistroID,Usuario,Acción,Campo,Valor previo,Valor actual");

            foreach (var record in auditoriaOrdenada)
            {
                string auditId = record.Id.ToString();

                string fecha = record.Contains("createdon")
                    ? ((DateTime)record["createdon"]).ToString("dd/MM/yyyy HH:mm:ss")
                    : "";

                string accion = "";
                if (record.Contains("action") && record["action"] is OptionSetValue actionOpt)
                    accion = AuditHelper.GetAuditActionLabel(actionOpt.Value);

                string logicalName = record.Contains("attributelogicalname")
                    ? record["attributelogicalname"].ToString()
                    : "";

                string campo = AuditHelper.GetDisplayName(_service, entidad, logicalName);

                string previo = record.Contains("oldvalue")
                    ? AuditHelper.InterpretValue(_service, record["oldvalue"], entidad, logicalName)
                    : "";

                string nuevo = record.Contains("newvalue")
                    ? AuditHelper.InterpretValue(_service, record["newvalue"], entidad, logicalName)
                    : "";

                string usuario = record.Contains("userid") && record["userid"] is EntityReference er
                    ? er.Name ?? ""
                    : "";

                writer.WriteLine($"{auditId},{fecha},{entidad},{registroId},{Escape(usuario)},{accion},{campo},{Escape(previo)},{Escape(nuevo)}");
            }
        }

        return nombreArchivo;
    }

    private string Escape(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        input = input.Replace("\r", " ").Replace("\n", " ");

        if (decimal.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal number))
            input = number.ToString("0.##", new CultureInfo("es-ES"));

        if (input.Contains(",") || input.Contains("\""))
            return $"\"{input.Replace("\"", "\"\"")}\"";

        return input;
    }
}
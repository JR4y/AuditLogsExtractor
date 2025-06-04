using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        string nombreArchivo = Path.Combine(_outputFolder, $"audit_{entidad}_{registroId}.csv");

        using (var writer = new StreamWriter(nombreArchivo, false, Encoding.UTF8))
        {
            writer.WriteLine("Fecha,Acción,Campo,Valor Anterior,Valor Nuevo,Usuario");

            foreach (var record in auditoria)
            {
                string fecha = record.Attributes.Contains("createdon")
                    ? ((DateTime)record["createdon"]).ToString("dd/MM/yyyy HH:mm:ss")
                    : "";

                string accion = "";
                if (record.Contains("action") && record["action"] is OptionSetValue actionOpt)
                {
                    accion = AuditHelper.GetAuditActionLabel(actionOpt.Value);
                }

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

                string usuario = record.Contains("userid")
                    ? ((EntityReference)record["userid"]).Name ?? ""
                    : "";

                writer.WriteLine($"{fecha},{accion},{campo},{Escape(previo)},{Escape(nuevo)},{usuario}");
            }
        }

        return nombreArchivo;
    }

    private string Escape(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        // Reemplazamos saltos de línea con espacio o algún separador
        input = input.Replace("\r", " ").Replace("\n", " ");

        // Si aún tiene comas o comillas, escapamos correctamente
        if (input.Contains(",") || input.Contains("\""))
        {
            return $"\"{input.Replace("\"", "\"\"")}\"";
        }

        return input;
    }
}

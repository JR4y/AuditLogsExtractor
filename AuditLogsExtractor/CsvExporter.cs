using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;

public class CsvExporter
{
    #region Fields and Constructor

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

    #endregion

    #region Public Methods

    public string ExportGroupAsCsv(string entityLogicalName, Guid recordId, List<Entity> auditRecords)
    {
        // Validaciones Iniciales 👇
        if (auditRecords == null || auditRecords.Count == 0)
            return null;

        if (recordId == Guid.Empty)
            return null;

        if (string.IsNullOrWhiteSpace(entityLogicalName))
            return null;


        string filePath = Path.Combine(_outputFolder, $"{recordId}.csv");
        var orderedAudit = auditRecords
            .Where(r => r.Attributes.Contains("createdon"))
            .OrderBy(r => r.GetAttributeValue<DateTime>("createdon"))
            .ToList();

        using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
        {
            writer.WriteLine("AuditID,Fecha,Entidad,RegistroID,Usuario,Acción,Campo,Valor previo,Valor actual");

            foreach (var record in orderedAudit)
            {
                string auditId = record.Id.ToString();

                string createdOn = record.Contains("createdon")
                    ? ((DateTime)record["createdon"]).ToString("dd/MM/yyyy HH:mm:ss")
                    : "";

                string actionLabel = "";
                if (record.Contains("action") && record["action"] is OptionSetValue actionOpt)
                {
                    actionLabel = AuditHelper.GetAuditActionLabel(actionOpt.Value);
                }

                string logicalFieldName = record.Contains("attributelogicalname")
                    ? record["attributelogicalname"].ToString()
                    : "";

                string fieldLabel = AuditHelper.GetDisplayName(_service, entityLogicalName, logicalFieldName);

                string oldValue = record.Contains("oldvalue")
                    ? AuditHelper.InterpretValue(_service, record["oldvalue"], entityLogicalName, logicalFieldName)
                    : "";

                string newValue = record.Contains("newvalue")
                    ? AuditHelper.InterpretValue(_service, record["newvalue"], entityLogicalName, logicalFieldName)
                    : "";

                string user = record.Contains("userid") && record["userid"] is EntityReference er
                    ? er.Name ?? ""
                    : "";

                writer.WriteLine($"{auditId},{createdOn},{entityLogicalName},{recordId},{Escape(user)},{actionLabel},{fieldLabel},{Escape(oldValue)},{Escape(newValue)}");
            }
        }

        return filePath;
    }

    #endregion

    #region Private Helpers

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

    #endregion
}
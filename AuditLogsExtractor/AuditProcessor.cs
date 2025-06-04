using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Crm.Sdk.Messages;
using System;
using System.Collections.Generic;

public class AuditProcessor
{
    private readonly IOrganizationService _service;

    public AuditProcessor(IOrganizationService service)
    {
        _service = service;
    }

    public Dictionary<Guid, List<Entity>> AgruparPorRegistroOrigen(IEnumerable<Entity> registrosAuditoria)
    {
        var resultado = new Dictionary<Guid, List<Entity>>();

        foreach (var record in registrosAuditoria)
        {
            if (!record.Attributes.Contains("objectid"))
                continue;

            var referencia = record.GetAttributeValue<EntityReference>("objectid");
            if (referencia == null || referencia.Id == Guid.Empty)
                continue;

            if (!resultado.ContainsKey(referencia.Id))
            {
                resultado[referencia.Id] = new List<Entity>();
            }

            resultado[referencia.Id].Add(record);
        }

        return resultado;
    }

    public List<Entity> ObtenerAuditoria(string entidad, int objectTypeCode, Guid recordId, DateTime fechaCorte)
    {
        var auditorias = new List<Entity>();

        var req = new RetrieveRecordChangeHistoryRequest
        {
            Target = new EntityReference(entidad, recordId)
        };

        var resp = (RetrieveRecordChangeHistoryResponse)_service.Execute(req);

        foreach (var detail in resp.AuditDetailCollection.AuditDetails)
        {
            if (detail is AttributeAuditDetail attrDetail)
            {
                var audit = attrDetail.AuditRecord;

                var createdOn = audit.GetAttributeValue<DateTime>("createdon");

                // 👉 Aquí aplicamos el corte
                if (createdOn >= fechaCorte)
                    continue;

                var action = audit.GetAttributeValue<OptionSetValue>("action")?.Value ?? -1;
                var user = audit.GetAttributeValue<EntityReference>("userid")?.Name ?? string.Empty;
                var objRef = audit.GetAttributeValue<EntityReference>("objectid");

                foreach (var attr in attrDetail.NewValue.Attributes)
                {
                    var record = new Entity("audit")
                    {
                        Id = audit.Id
                    };

                    record["createdon"] = createdOn;
                    record["action"] = new OptionSetValue(action);
                    record["userid"] = new EntityReference("systemuser", audit.GetAttributeValue<EntityReference>("userid")?.Id ?? Guid.Empty) { Name = user };
                    record["objectid"] = objRef;
                    record["attributelogicalname"] = attr.Key;

                    object oldVal = attrDetail.OldValue != null && attrDetail.OldValue.Contains(attr.Key)
                        ? attrDetail.OldValue[attr.Key]
                        : null;

                    record["oldvalue"] = oldVal;
                    record["newvalue"] = attr.Value;

                    auditorias.Add(record);
                }
            }
        }

        return auditorias;
    }

}
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;

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
            var audit = detail.AuditRecord;
            var createdOn = audit.GetAttributeValue<DateTime>("createdon");
            if (createdOn >= fechaCorte)
                continue;

            int actionCode = audit.Attributes.Contains("action") && audit["action"] is OptionSetValue opt
                ? opt.Value
                : InferRelationshipAction(detail);

            var userRef = audit.GetAttributeValue<EntityReference>("userid");
            var objRef = audit.GetAttributeValue<EntityReference>("objectid");

            if (detail is AttributeAuditDetail attrDetail)
            {
                foreach (var attr in attrDetail.NewValue.Attributes)
                {
                    var record = new Entity("audit")
                    {
                        Id = audit.Id
                    };

                    record["createdon"] = createdOn;
                    record["action"] = new OptionSetValue(actionCode); // ✅ como OptionSetValue
                    record["userid"] = new EntityReference("systemuser", userRef?.Id ?? Guid.Empty)
                    {
                        Name = userRef?.Name ?? string.Empty
                    };
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
            else if (detail is RelationshipAuditDetail relDetail)
            {
                // 🔥 EXCLUIR asociaciones para mejorar legibilidad y rendimiento
                continue;
                var record = new Entity("audit")
                {
                    Id = audit.Id
                };

                string tipoRelacion = relDetail.RelationshipName ?? "(sin nombre)";
                string idsAsociados = relDetail.TargetRecords != null
                    ? string.Join(", ", relDetail.TargetRecords.Select(x => x.Id.ToString()))
                    : "(vacío)";

                string resumen = actionCode == 34
                    ? $"Desasociados: {idsAsociados}"
                    : $"Asociados: {idsAsociados}";

                record["createdon"] = createdOn;
                record["action"] = new OptionSetValue(actionCode); // ✅ como OptionSetValue
                record["userid"] = new EntityReference("systemuser", userRef?.Id ?? Guid.Empty)
                {
                    Name = userRef?.Name ?? string.Empty
                };
                record["objectid"] = objRef;
                record["attributelogicalname"] = tipoRelacion;
                record["oldvalue"] = null;
                record["newvalue"] = resumen;

                auditorias.Add(record);
            }
        }

        return auditorias;
    }

    private int InferRelationshipAction(AuditDetail detail)
    {
        if (detail is RelationshipAuditDetail rel)
        {
            return (rel.TargetRecords?.Count ?? 0) > 0 ? 12 : 13;
        }
        return -1;
    }
}
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;

public class AuditProcessor
{
    private readonly IOrganizationService _service;

    #region Constructor

    public AuditProcessor(IOrganizationService service)
    {
        _service = service;
    }

    #endregion

    #region Public Methods

    public Dictionary<Guid, List<Entity>> GroupByOriginRecord(IEnumerable<Entity> auditRecords)
    {
        var result = new Dictionary<Guid, List<Entity>>();

        foreach (var record in auditRecords)
        {
            if (!record.Attributes.Contains("objectid"))
                continue;

            var reference = record.GetAttributeValue<EntityReference>("objectid");
            if (reference == null || reference.Id == Guid.Empty)
                continue;

            if (!result.ContainsKey(reference.Id))
            {
                result[reference.Id] = new List<Entity>();
            }

            result[reference.Id].Add(record);
        }

        return result;
    }

    public List<Entity> GetAuditRecords(string entityName, int objectTypeCode, Guid recordId, DateTime cutoffDate)
    {
        var audits = new List<Entity>();

        var request = new RetrieveRecordChangeHistoryRequest
        {
            Target = new EntityReference(entityName, recordId)
        };

        var response = (RetrieveRecordChangeHistoryResponse)_service.Execute(request);

        foreach (var detail in response.AuditDetailCollection.AuditDetails)
        {
            var audit = detail.AuditRecord;
            var createdOn = audit.GetAttributeValue<DateTime>("createdon");
            if (createdOn >= cutoffDate)
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
                    var auditEntity = new Entity("audit")
                    {
                        Id = audit.Id
                    };

                    auditEntity["createdon"] = createdOn;
                    auditEntity["action"] = new OptionSetValue(actionCode);
                    auditEntity["userid"] = new EntityReference("systemuser", userRef?.Id ?? Guid.Empty)
                    {
                        Name = userRef?.Name ?? string.Empty
                    };
                    auditEntity["objectid"] = objRef;
                    auditEntity["attributelogicalname"] = attr.Key;

                    object oldVal = attrDetail.OldValue != null && attrDetail.OldValue.Contains(attr.Key)
                        ? attrDetail.OldValue[attr.Key]
                        : null;

                    auditEntity["oldvalue"] = oldVal;
                    auditEntity["newvalue"] = attr.Value;

                    audits.Add(auditEntity);
                }
            }
            // Sección de RelationshipAuditDetail desactivada explícitamente
        }

        return audits;
    }

    #endregion

    #region Private Helpers

    private int InferRelationshipAction(AuditDetail detail)
    {
        if (detail is RelationshipAuditDetail rel)
        {
            return (rel.TargetRecords?.Count ?? 0) > 0 ? 12 : 13;
        }
        return -1;
    }

    #endregion
}
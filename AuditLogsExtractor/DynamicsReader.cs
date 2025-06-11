using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;

public class DynamicsReader
{
    private readonly ServiceClient _client;

    #region Constructor

    public DynamicsReader(string connectionString)
    {
        _client = new ServiceClient(connectionString);
        if (!_client.IsReady)
        {
            throw new Exception("❌ Failed to connect to Dynamics 365.");
        }
    }

    #endregion

    #region Configuration Reading

    public Dictionary<string, string> GetConfigurationParameters(string logicalName = "new_configuracion")
    {
        var parameters = new Dictionary<string, string>();

        var query = new QueryExpression(logicalName)
        {
            TopCount = 1,
            ColumnSet = new ColumnSet(true)
        };

        EntityCollection results = _client.RetrieveMultiple(query);
        if (results.Entities.Count == 0)
        {
            throw new Exception("⚠️ No configuration record found.");
        }

        var config = results.Entities[0];

        parameters["months_to_keep"] = config.GetAttributeValue<int?>("lyn_meses_conservar")?.ToString() ?? "0";
        parameters["sp_site"] = config.GetAttributeValue<string>("lyn_sp_site") ?? string.Empty;
        parameters["sp_upload_folder"] = config.GetAttributeValue<string>("lyn_sp_upload_folder") ?? string.Empty;
        parameters["sp_user"] = config.GetAttributeValue<string>("lyn_sp_user") ?? string.Empty;
        parameters["sp_password"] = config.GetAttributeValue<string>("lyn_sp_password") ?? string.Empty;

        parameters["d365_clientid"] = config.GetAttributeValue<string>("lyn_d365_clientid") ?? string.Empty;
        parameters["d365_clientsecret"] = config.GetAttributeValue<string>("lyn_d365_clientsecret") ?? string.Empty;
        parameters["d365_tenantid"] = config.GetAttributeValue<string>("lyn_d365_tenantid") ?? string.Empty;
        parameters["d365_orgurl"] = config.GetAttributeValue<string>("lyn_d365_orgurl") ?? string.Empty;

        bool zipMode = config.GetAttributeValue<bool>("lyn_zip_uploadmode");
        parameters["zip_upload_mode"] = zipMode ? "true" : "false";

        parameters["configuration_id"] = config.Id.ToString();

        var entityQuery = new QueryExpression("lyn_entidad_auditadas")
        {
            ColumnSet = new ColumnSet("lyn_logicalname", "lyn_objecttypecode", "lyn_habilitado"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("lyn_configuracion", ConditionOperator.Equal, config.Id),
                    new ConditionExpression("lyn_habilitado", ConditionOperator.Equal, true)
                }
            }
        };

        var entityResults = _client.RetrieveMultiple(entityQuery);

        int i = 1;
        foreach (var entity in entityResults.Entities)
        {
            var logicalNameValue = entity.GetAttributeValue<string>("lyn_logicalname");
            var objectTypeCode = entity.GetAttributeValue<int>("lyn_objecttypecode");

            if (!string.IsNullOrEmpty(logicalName))
            {
                parameters[$"entity_{i}_logicalname"] = logicalNameValue;
                parameters[$"entity_{i}_otc"] = objectTypeCode.ToString();
                i++;
            }
        }

        parameters["total_entities"] = (i - 1).ToString();

        return parameters;
    }

    #endregion

    #region Record Query

    public List<Guid> GetRecordIds(string entityLogicalName, DateTime cutoffDate)
    {
        var ids = new List<Guid>();

        int pageNumber = 1;
        string pagingCookie = null;
        bool moreRecords = true;

        while (moreRecords)
        {
            var query = new QueryExpression(entityLogicalName)
            {
                ColumnSet = new ColumnSet("createdon"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("createdon", ConditionOperator.OnOrBefore, cutoffDate)
                    }
                },
                PageInfo = new PagingInfo
                {
                    Count = 5000,
                    PageNumber = pageNumber,
                    PagingCookie = pagingCookie
                }
            };

            EntityCollection results = _client.RetrieveMultiple(query);

            foreach (var entity in results.Entities)
            {
                if (entity.Id != Guid.Empty)
                    ids.Add(entity.Id);
            }

            moreRecords = results.MoreRecords;
            if (moreRecords)
            {
                pageNumber++;
                pagingCookie = results.PagingCookie;
            }
        }

        return ids;
    }

    #endregion

    #region Service Accessor

    public IOrganizationService GetService() => _client;

    #endregion
}
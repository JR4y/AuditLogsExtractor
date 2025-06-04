using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;

public class DynamicsReader
{
    private readonly ServiceClient _client;

    public DynamicsReader(string connectionString)
    {
        _client = new ServiceClient(connectionString);
        if (!_client.IsReady)
        {
            throw new Exception("❌ No se pudo establecer conexión con Dynamics 365.");
        }
    }

    public Dictionary<string, string> ObtenerParametrosDeConfiguracion(string logicalName = "new_configuracion")
    {
        var parametros = new Dictionary<string, string>();

        var query = new QueryExpression(logicalName)
        {
            TopCount = 1,
            ColumnSet = new ColumnSet(true)
        };

        EntityCollection results = _client.RetrieveMultiple(query);
        if (results.Entities.Count == 0)
        {
            throw new Exception("⚠️ No se encontró ningún registro de configuración.");
        }

        var config = results.Entities[0];

        parametros["meses_conservar"] = config.GetAttributeValue<int?>("lyn_meses_conservar")?.ToString() ?? "0";
        parametros["sp_site"] = config.GetAttributeValue<string>("lyn_sp_site") ?? string.Empty;
        parametros["sp_upload_folder"] = config.GetAttributeValue<string>("lyn_sp_upload_folder") ?? string.Empty;
        parametros["sp_user"] = config.GetAttributeValue<string>("lyn_sp_user") ?? string.Empty;
        parametros["sp_password"] = config.GetAttributeValue<string>("lyn_sp_password") ?? string.Empty;

        parametros["d365_clientid"] = config.GetAttributeValue<string>("lyn_d365_clientid") ?? string.Empty;
        parametros["d365_clientsecret"] = config.GetAttributeValue<string>("lyn_d365_clientsecret") ?? string.Empty;
        parametros["d365_tenantid"] = config.GetAttributeValue<string>("lyn_d365_tenantid") ?? string.Empty;
        parametros["d365_orgurl"] = config.GetAttributeValue<string>("lyn_d365_orgurl") ?? string.Empty;

        // Guardar el ID del registro de configuración
        parametros["configuracion_id"] = config.Id.ToString();

        // Consultar entidades auditadas relacionadas a este registro de configuración
        var queryEntidades = new QueryExpression("lyn_entidad_auditadas")
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

        var resultadoEntidades = _client.RetrieveMultiple(queryEntidades);

        int i = 1;
        foreach (var entidad in resultadoEntidades.Entities)
        {
            var ln = entidad.GetAttributeValue<string>("lyn_logicalname");
            var objectTypeCode = entidad.GetAttributeValue<int>("lyn_objecttypecode");

            if (!string.IsNullOrEmpty(logicalName))
            {
                parametros[$"entidad_{i}_logicalname"] = ln;
                parametros[$"entidad_{i}_otc"] = objectTypeCode.ToString();
                i++;
            }
        }

        parametros["total_entidades"] = (i - 1).ToString();

        return parametros;
    }

    public List<Guid> ObtenerRecordIds(string entidad, DateTime fechaCorte)
    {
        var ids = new List<Guid>();

        int pageNumber = 1;
        string pagingCookie = null;
        bool moreRecords = true;

        while (moreRecords)
        {
            var query = new QueryExpression(entidad)
            {
                ColumnSet = new ColumnSet("createdon"), // o ColumnSet(false) si no necesitas nada
                Criteria = new FilterExpression
                {
                    Conditions =
                {
                    new ConditionExpression("createdon", ConditionOperator.OnOrBefore, fechaCorte)
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

    public IOrganizationService ObtenerServicio() => _client;
}
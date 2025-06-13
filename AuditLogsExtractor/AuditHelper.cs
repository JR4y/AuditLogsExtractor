using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using System;
using System.Collections.Generic;

    namespace AuditLogsExtractor
{
    public static class AuditHelper
    {
        #region Caches

        private static readonly Dictionary<string, Dictionary<int, string>> OptionSetCache = new Dictionary<string, Dictionary<int, string>>();
        private static readonly Dictionary<string, Dictionary<string, string>> AttributeLabelCache = new Dictionary<string, Dictionary<string, string>>();

        #endregion

        #region Public Methods

        public static string InterpretValue(IOrganizationService service, object value, string entityLogicalName, string fieldName)
        {
            if (value == null) return string.Empty;

            if (value is OptionSetValue)
            {
                var opt = (OptionSetValue)value;
                return GetOptionLabel(service, entityLogicalName, fieldName, opt.Value);
            }
            if (value is EntityReference)
            {
                var er = (EntityReference)value;
                return er.Name ?? er.Id.ToString();
            }
            if (value is Money)
            {
                var money = (Money)value;
                return money.Value.ToString("F2");
            }

            return value.ToString();
        }

        public static string GetOptionLabel(IOrganizationService service, string entityLogicalName, string fieldName, int value)
        {
            string cacheKey = entityLogicalName + "." + fieldName;

            if (OptionSetCache.ContainsKey(cacheKey) && OptionSetCache[cacheKey].ContainsKey(value))
                return OptionSetCache[cacheKey][value];

            var options = new Dictionary<int, string>();

            try
            {
                var req = new RetrieveAttributeRequest
                {
                    EntityLogicalName = entityLogicalName,
                    LogicalName = fieldName,
                    RetrieveAsIfPublished = true
                };

                var response = (RetrieveAttributeResponse)service.Execute(req);
                var metadata = response.AttributeMetadata as EnumAttributeMetadata;

                if (metadata != null && metadata.OptionSet != null)
                {
                    foreach (var option in metadata.OptionSet.Options)
                    {
                        options[option.Value.Value] = option.Label.UserLocalizedLabel != null
                            ? option.Label.UserLocalizedLabel.Label
                            : "(no label)";
                    }
                }

                if (metadata != null && metadata.OptionSet.IsGlobal == true && metadata.OptionSet.Name != null)
                {
                    var globalReq = new RetrieveOptionSetRequest { Name = metadata.OptionSet.Name };
                    var globalResp = (RetrieveOptionSetResponse)service.Execute(globalReq);
                    var globalMeta = globalResp.OptionSetMetadata as OptionSetMetadata;

                    foreach (var option in globalMeta.Options)
                    {
                        options[option.Value.Value] = option.Label.UserLocalizedLabel != null
                            ? option.Label.UserLocalizedLabel.Label
                            : "(no label)";
                    }
                }
            }
            catch
            {
                return value.ToString();
            }

            OptionSetCache[cacheKey] = options;
            return options.ContainsKey(value) ? options[value] + " (" + value + ")" : value.ToString();
        }

        public static string GetDisplayName(IOrganizationService service, string entityLogicalName, string fieldName)
        {
            string cacheKey = entityLogicalName;

            if (AttributeLabelCache.ContainsKey(cacheKey) && AttributeLabelCache[cacheKey].ContainsKey(fieldName))
                return AttributeLabelCache[cacheKey][fieldName];

            var labels = new Dictionary<string, string>();

            try
            {
                var req = new RetrieveEntityRequest
                {
                    LogicalName = entityLogicalName,
                    EntityFilters = EntityFilters.Attributes,
                    RetrieveAsIfPublished = true
                };

                var res = (RetrieveEntityResponse)service.Execute(req);

                foreach (var attr in res.EntityMetadata.Attributes)
                {
                    if (!string.IsNullOrEmpty(attr.LogicalName))
                    {
                        labels[attr.LogicalName] = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName;
                    }
                }
            }
            catch
            {
                return fieldName;
            }

            AttributeLabelCache[cacheKey] = labels;
            return labels.ContainsKey(fieldName) ? labels[fieldName] : fieldName;
        }

        public static string GetAuditActionLabel(int? actionCode)
        {
            if (!actionCode.HasValue)
                return "(sin acción)";

            if (AuditActionLabels.TryGetValue(actionCode.Value, out var label))
                return label;

            return string.Format("Acción desconocida ({0})", actionCode.Value);
        }

        #endregion

        #region Internal Audit Labels

        private static readonly Dictionary<int, string> AuditActionLabels = new Dictionary<int, string>
    {
        { 1, "Crear" },
        { 2, "Actualizar" },
        { 3, "Eliminar" },
        { 4, "Activar" },
        { 5, "Desactivar" },
        { 6, "Asignar" },
        { 7, "Compartir" },
        { 8, "Reasignar" },
        { 9, "Quitar compartición" },
        { 10, "Combinar" },
        { 11, "Actualizar estado" },
        { 12, "Asociar" },
        { 13, "Desasociar" }
    };

        #endregion
    }

}
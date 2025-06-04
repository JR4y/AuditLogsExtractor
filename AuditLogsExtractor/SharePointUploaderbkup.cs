using System;
using System.IO;
using System.Net;

public class SharePointUploader
{
    private readonly string _siteUrl;
    private readonly string _uploadFolder;
    private readonly string _username;
    private readonly string _password;

    public SharePointUploader(string siteUrl, string uploadFolder, string username, string password)
    {
        _siteUrl = siteUrl;
        _uploadFolder = uploadFolder.TrimEnd('/');
        _username = username;
        _password = password;
    }

    public void SubirArchivo(string rutaLocal, string entidad, Guid registroId)
    {
        string subcarpeta = registroId.ToString().Substring(0, 2);
        string destino = $"{_uploadFolder}/{entidad}/{subcarpeta}";
        string nombreArchivo = Path.GetFileName(rutaLocal);
        string urlFinal = $"{_siteUrl}/_api/web/GetFolderByServerRelativeUrl('{destino}')/Files/add(url='{nombreArchivo}',overwrite=true)";

        Logger.Info("📤 Preparando subida a SharePoint...");
        Logger.Info($"🌍 URL final completa: {urlFinal}");
        Logger.Info($"📁 Ruta destino (serverRelative): {destino.Replace("%20", " ")}");
        Logger.Info($"📎 Archivo a subir: {nombreArchivo}");

        Logger.Info("🔐 Iniciando autenticación SAML...");
        Logger.Info($"🔗 Endpoint STS: https://login.microsoftonline.com/extSTS.srf");
        Logger.Info($"🌐 siteUrl: {_siteUrl}");
        Logger.Info($"👤 Usuario: {_username}");

        try
        {
            var cookies = AuditHelper.ObtenerCookieSamlAuth(_siteUrl, _username, _password);
            Logger.Info("✅ Autenticación SAML completada.");

            // 🧱 CREACIÓN DE CARPETA SI NO EXISTE
            string urlCrearCarpeta = $"{_siteUrl}/_api/web/folders";
            string formDigest = AuditHelper.ObtenerFormDigest(_siteUrl, cookies);
            string folderName = destino;

            var folderRequest = (HttpWebRequest)WebRequest.Create(urlCrearCarpeta);
            folderRequest.Method = "POST";
            folderRequest.CookieContainer = cookies;
            folderRequest.Headers.Add("X-RequestDigest", formDigest);
            folderRequest.ContentType = "application/json;odata=verbose";
            folderRequest.Accept = "application/json;odata=verbose";

            string folderPayload = $@"{{ '__metadata': {{ 'type': 'SP.Folder' }}, 'ServerRelativeUrl': '{folderName}' }}";
            byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(folderPayload);
            folderRequest.ContentLength = payloadBytes.Length;

            using (var stream = folderRequest.GetRequestStream())
                stream.Write(payloadBytes, 0, payloadBytes.Length);

            try
            {
                using (var folderResponse = (HttpWebResponse)folderRequest.GetResponse())
                {
                    Logger.Info("✅ Carpeta creada o ya existente.");
                }
            }
            catch (WebException ex)
            {
                Logger.Warning("⚠️ La carpeta podría ya existir. Continuamos con la subida...");
            }

            // 🔄 SUBIDA DEL ARCHIVO
            byte[] contenido = File.ReadAllBytes(rutaLocal);
            var request = (HttpWebRequest)WebRequest.Create(urlFinal);
            request.CookieContainer = cookies;
            request.Method = "POST";
            request.ContentLength = contenido.Length;
            request.ContentType = "application/octet-stream";
            request.Headers.Add("X-FORMS_BASED_AUTH_ACCEPTED", "f");
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

            using (var stream = request.GetRequestStream())
                stream.Write(contenido, 0, contenido.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                Logger.Info($"📦 Estado de respuesta: {response.StatusCode}");
                if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
                    throw new Exception($"❌ Error al subir a SharePoint: {response.StatusCode}");
            }
        }
        catch (WebException ex)
        {
            Logger.Error("❌ Error durante la subida a SharePoint.");
            if (ex.Response != null)
            {
                using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                    Logger.Error($"📄 Detalle del error HTTP:\n{reader.ReadToEnd()}");
            }
            else
            {
                Logger.Error(ex.Message);
            }
            throw;
        }
    }

    private void AsegurarCarpetaSharePoint(string relativePath, CookieContainer cookies)
    {
        string apiUrl = $"{_siteUrl}/_api/web/folders";
        Logger.Info($"📁 Verificando/creando carpeta: {relativePath}");

        var request = (HttpWebRequest)WebRequest.Create(apiUrl);
        request.Method = "POST";
        request.CookieContainer = cookies;
        request.ContentType = "application/json;odata=verbose";
        request.Headers.Add("X-RequestDigest", AuditHelper.ObtenerFormDigest(_siteUrl, cookies));
        request.Accept = "application/json;odata=verbose";

        string jsonBody = $"{{ '__metadata': {{ 'type': 'SP.Folder' }}, 'ServerRelativeUrl': '{relativePath}' }}";
        byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        request.ContentLength = bodyBytes.Length;

        using (var stream = request.GetRequestStream())
        {
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }

        using (var response = (HttpWebResponse)request.GetResponse())
        {
            Logger.Info($"📁 Carpeta verificada o creada: {relativePath} ({response.StatusCode})");
        }
    }
}
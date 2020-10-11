using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Client for Express interaction
    /// </summary>
    public class ExpressClient : IDisposable
    {
        private readonly HttpClient _httpClient;

        private readonly HttpClientHandler _httpClientHandler;

        public Uri BaseUri => _httpClient.BaseAddress;

        private const string AuthCookieName = "autodontix";

        public Cookie AuthCookie => _httpClientHandler.CookieContainer.GetCookies(_httpClient.BaseAddress)
            .FirstOrDefault(c => c.Name == AuthCookieName);

        /// <summary>
        /// construct a client
        /// </summary>
        /// <param name="baseUri">URI of Express site</param>
        public ExpressClient(Uri baseUri)
        {
            _httpClientHandler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                UseDefaultCredentials = true,
            };

            _httpClient = new HttpClient(_httpClientHandler)
            {
                BaseAddress = baseUri,
            };
        }

        /// <summary>
        /// check whether loggin in from previous cookie (persistent)
        /// </summary>
        /// <returns>whether loggin in</returns>
        public async Task<bool> IsLoggedIn(Cookie storedCookie)
        {
            try
            {
                if (storedCookie == null)
                    return false;

                if (storedCookie.Name != AuthCookieName || storedCookie.Expired)
                    return false;

                _httpClientHandler.CookieContainer.Add(_httpClient.BaseAddress, storedCookie); // will replace any existing one

                // make sure it works. may be first call, so must update anti-forgery first.
                await RefreshAntiforgeryToken();
                var response = await _httpClient.GetAsync("/Home/Ping");

                if (response.IsSuccessStatusCode)
                    return true;

                if (AuthCookie != null)
                    AuthCookie.Expired = true; // "delete" what we added
                return false;
            }
            catch (Exception)
            {
                if (AuthCookie != null)
                    AuthCookie.Expired = true; // "delete"
                return false;
            }
        }

        /// <summary>
        /// POCO for serialization
        /// </summary>
        public class TokenData
        {
            public string token { get; set; }
            public string tokenName { get; set; }
        }

        /// <summary>
        /// POCO for deserialization
        /// </summary>
        public class ResultData
        {
            /// <summary> id </summary>
            string eid { get; set; }
            /// <summary> when order was uploaded, if at all </summary>
            DateTime? CreatedUtc { get; set; }
            /// <summary> code of any status </summary>
            int? Status { get; set; }
        }

        /// <summary>
        /// for <see cref="ResultData.Status"/>. True if new and now yet reviewed or reviewed but left undecided
        /// </summary>
        public static bool StatusIsReadyForReview(int st) => st == 0 || st == 3;

        /// <summary>
        /// for <see cref="ResultData.Status"/>
        /// </summary>
        public static bool StatusIsAcceptedDownloaded(int st) => st == 1;

        /// <summary>
        /// for <see cref="ResultData.Status"/>
        /// </summary>
        public static bool StatusIsRejected(int st) => st == 2;

        /// <summary>
        /// for <see cref="ResultData.Status"/>
        /// </summary>
        public static bool StatusIsInProgress(int st) => st == 10;

        /// <summary>
        /// for <see cref="ResultData.Status"/>. Can be true for various reasons
        /// </summary>
        public static bool StatusIsFailure(int st) => st == -3 || st == -2 || st == -1 || st == 11 || st == 12;

        /// <summary>
        /// for <see cref="ResultData.Status"/>
        /// </summary>
        public static bool StatusIsForward(int st) => st == 20 || st == 21 || st == 22 || st == 29;


        /// <summary>
        /// refresh for current or optionally existing identity. If success, add the cookie to the default headers
        /// </summary>
        private async Task RefreshAntiforgeryToken()
        {
            var responseXsrf = await _httpClient.GetAsync("api/xsrf/get/");
            if (!responseXsrf.IsSuccessStatusCode)
                throw new Exception($"Cannot get af token [Error code {responseXsrf.StatusCode}]");

            var sXsrf = await responseXsrf.Content.ReadAsStringAsync();
            var td = JsonConvert.DeserializeObject<TokenData>(sXsrf);

            if (_httpClient.DefaultRequestHeaders.Contains(td.tokenName))
                _httpClient.DefaultRequestHeaders.Remove(td.tokenName);
            _httpClient.DefaultRequestHeaders.Add(td.tokenName, td.token);
        }


        /// <summary>
        /// throws on failure. does not catch
        /// </summary>
        /// <param name="email">identifies user/customer</param>
        /// <param name="password">user's/customer's secret</param>
        /// <param name="remember">whether to store authentication as persistent cookie</param>
        public async Task Login(string email, string password, bool remember)
        {
            var postData = new FormUrlEncodedContent(
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("email", email),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("remember", remember.ToString())
                }
            );

            await RefreshAntiforgeryToken();

            var response = await _httpClient.PostAsync("Home/LoginApi", postData);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Cannot request login [Error code {response.StatusCode}]");

            // we have a user claim now, so must refresh token
            // https://www.blinkingcaret.com/2018/11/29/asp-net-core-web-api-antiforgery/
            await RefreshAntiforgeryToken();
        }

        /// <summary>
        /// log out any current user
        /// </summary>
        public async Task Logout()
        {
            // no antiforgery token needed
            await _httpClient.GetAsync("home/logout/");
        }

        /// <summary>
        /// get status of order as identified by name
        /// </summary>
        /// <param name="orderName">order name</param>
        /// <returns>a list of result data, status and created time for each. There can be multiple if the order has been uploaded
        /// multiple times. The list can also be empty if there are no such orders. Null is returned on any error.</returns>
        public async Task<List<ResultData>> GetStatus(string orderName)
        {
            try
            {
                var postData = new FormUrlEncodedContent(
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("orderName", orderName),
                    });

                var response = await _httpClient.PostAsync("api/Results/ForOrder", postData);
                if (!response.IsSuccessStatusCode)
                    return null;

                if (response.StatusCode == HttpStatusCode.NoContent)
                    return new List<ResultData>();

                var sResp = await response.Content.ReadAsStringAsync();
                var resultDatas = JsonConvert.DeserializeObject<List<ResultData>>(sResp);
                return resultDatas;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// see if order can be modeled
        /// </summary>
        /// <param name="orderText">the xml of the order as a string</param>
        /// <returns>empty if good, otherwise message for why not</returns>
        public async Task<string> Qualify(string orderText)
        {
            try
            {
                var textContent = new StringContent(orderText, Encoding.UTF8, "text/xml");
                var formContent = new MultipartFormDataContent { { textContent, "file", "order.xml" } };
                var response = await _httpClient.PostAsync("api/Qualification/Qualify", formContent);
                if (!response.IsSuccessStatusCode)
                    return $"Could not qualify order [Error code {response.StatusCode}]";

                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public async Task<string> Upload(string orderFilename, MemoryStream zipStream)
        {
            try
            {
                var streamContent = new StreamContent(zipStream);
                var formContent = new MultipartFormDataContent { { streamContent, "file", orderFilename } };
                var response = await _httpClient.PostAsync("api/Streaming/Upload/", formContent);
                return !response.IsSuccessStatusCode ? $"Could not upload order [Error code {response.StatusCode}]" : "";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _httpClientHandler?.Dispose(); // safe to dispose both
        }
    }
}

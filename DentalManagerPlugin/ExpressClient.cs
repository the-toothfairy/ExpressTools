using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
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

        private const string AuthCookieName = "autodontix";

        public Cookie AuthCookie => _httpClientHandler.CookieContainer.GetCookies(_httpClient.BaseAddress)
            .FirstOrDefault(c => c.Name == AuthCookieName);

        public string UriString => _httpClient.BaseAddress.Authority;

        /// <summary>
        /// not allowed, use other overload
        /// </summary>
        private ExpressClient() { }

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
        /// check whether log in from previous cookie (persistent) is still valid.
        /// </summary>
        public async Task<bool> CheckIfStillLoggedIn(Cookie storedCookie)
        {
            try
            {
                if (storedCookie == null)
                    return false;

                if (storedCookie.Name != AuthCookieName || storedCookie.Expired)
                    return false;

                //  Not if too little time left.
                if (storedCookie.Expires < DateTime.UtcNow.AddMinutes(-15))
                    return false;

                _httpClientHandler.CookieContainer.Add(_httpClient.BaseAddress, storedCookie); // will replace any existing one

                // make sure it works. may be first call, so must update anti-forgery first.
                await RefreshAntiforgeryToken();
                var response = await _httpClient.GetAsync("/Home/Ping");

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

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
            /// <summary> to allow int to bool conversion for the fields named "Is..." </summary>
            public const int TRUE = 1;

            /// <summary> id </summary>
            public string eid { get; set; }
            /// <summary> when order was uploaded, if at all </summary>
            public DateTime? CreatedUtc { get; set; }
            /// <summary> when order was reviewed, if at all </summary>
            public DateTime? ReviewedUtc { get; set; }

            public string StatusMessage { get; set; }

            public int IsDecided { get; set; }
            public int IsFailed { get; set; }
            public int IsViewable { get; set; }
            public int IsNew { get; set; }
            public int IsForwarded { get; set; }
        }

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
        /// returns whether success. does not catch
        /// </summary>
        /// <param name="email">identifies user/customer</param>
        /// <param name="password">user's/customer's secret</param>
        /// <param name="remember">whether to store authentication as persistent cookie</param>
        public async Task<bool> Login(string email, string password, bool remember)
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
                return false;

            // we have a user claim now, so must refresh token
            // https://www.blinkingcaret.com/2018/11/29/asp-net-core-web-api-antiforgery/
            await RefreshAntiforgeryToken();

            return true;
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
        /// get status of order as identified by name. no try/catch.
        /// </summary>
        /// <param name="orderName">order name</param>
        /// <returns>a list of result data, status and created time for each. There can be multiple if the order has been uploaded
        /// multiple times. The list can also be empty if there are no such orders.</returns>
        public async Task<List<ResultData>> GetStatus(string orderName)
        {
            var postData = new FormUrlEncodedContent(
               new List<KeyValuePair<string, string>>
               {
                    new KeyValuePair<string, string>("orderName", orderName),
               });

            var response = await _httpClient.PostAsync("api/Results/ForOrder", postData);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Could not get status for order [Error code {response.StatusCode}]");

            if (response.StatusCode == HttpStatusCode.NoContent)
                return new List<ResultData>();

            var sResp = await response.Content.ReadAsStringAsync();
            var resultDatas = JsonConvert.DeserializeObject<List<ResultData>>(sResp);
            return resultDatas;
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

        public async Task Upload(string orderFilename, MemoryStream zipStream, CancellationToken cancelToken)
        {
            var streamContent = new StreamContent(zipStream);
            var formContent = new MultipartFormDataContent { { streamContent, "file", orderFilename } };
            var response = await _httpClient.PostAsync("api/Streaming/Upload/", formContent, cancelToken);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Could not upload order [Error code {response.StatusCode}]");
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _httpClientHandler?.Dispose(); // safe to dispose both
        }
    }
}

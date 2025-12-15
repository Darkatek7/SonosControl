using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests
{
    public class SonosConnectorRepoTests
    {
        [Fact]
        public async Task CreateGroup_SendsCorrectSoapRequest()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            var settingsRepoMock = new Mock<ISettingsRepo>();

            var masterIp = "192.168.1.10";
            var slaveIp = "192.168.1.11";
            var masterRinconId = "ABC1234567890";
            var masterUuid = $"uuid:RINCON_{masterRinconId}";

            // Mock response for GetRinconIdAsync (Master)
            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req =>
                        req.Method == HttpMethod.Get &&
                        req.RequestUri.ToString() == $"http://{masterIp}:1400/xml/device_description.xml"),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent($"<root><device><UDN>{masterUuid}</UDN></device></root>")
                });

            // Mock response for GetRinconIdAsync (Slave)
             mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req =>
                        req.Method == HttpMethod.Get &&
                        req.RequestUri.ToString() == $"http://{slaveIp}:1400/xml/device_description.xml"),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent($"<root><device><UDN>uuid:RINCON_000000000002</UDN></device></root>")
                });


            // Mock response for SetAVTransportURI (the grouping command)
            // We use Callback to inspect content before it's disposed
            bool correctSoapRequestSent = false;

            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req =>
                        req.Method == HttpMethod.Post &&
                        req.RequestUri.ToString() == $"http://{slaveIp}:1400/MediaRenderer/AVTransport/Control"
                    ),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Callback<HttpRequestMessage, CancellationToken>((req, token) =>
                {
                    var content = req.Content.ReadAsStringAsync().Result;
                    if (content.Contains($"x-rincon:RINCON_{masterRinconId}") &&
                        content.Contains("<CurrentURIMetaData></CurrentURIMetaData>"))
                    {
                        correctSoapRequestSent = true;
                    }
                })
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            httpClientFactoryMock.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var repo = new SonosConnectorRepo(httpClientFactoryMock.Object, settingsRepoMock.Object);

            // Act
            await repo.CreateGroup(masterIp, new[] { slaveIp });

            // Assert
            Assert.True(correctSoapRequestSent, "The SOAP request content did not match the expected format.");
        }
    }
}

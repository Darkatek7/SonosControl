using Moq;
using System.Net.Http;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;

namespace SonosControl.Tests.Mocks
{
    public static class MockFactory
    {
        public static ISettingsRepo CreateMockSettingsRepo()
        {
            var mock = new Mock<ISettingsRepo>();
            return mock.Object;
        }
    }
}

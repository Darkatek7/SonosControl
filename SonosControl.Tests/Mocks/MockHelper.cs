using Moq;
using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;

namespace SonosControl.Tests.Mocks
{
    public static class MockHelper
    {
        public static IServiceScopeFactory CreateScopeFactory(IUnitOfWork uow)
        {
            var serviceScope = new Mock<IServiceScope>();
            var serviceProvider = new Mock<IServiceProvider>();

            serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(uow);
            serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

            var serviceScopeFactory = new Mock<IServiceScopeFactory>();
            serviceScopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);

            return serviceScopeFactory.Object;
        }
    }
}

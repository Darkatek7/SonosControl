//var startTime = new TimeOnly(6, 0);
//var stopTime = new TimeOnly(17, 0);
//var timeNow = new TimeOnly(17, 1);
//
//var timeDifference = startTime - timeNow;
//
//if (timeNow >= stopTime || startTime >= timeNow)
//{
//    var delayInMs = (int)timeDifference.TotalMilliseconds;
//
//    Console.WriteLine("Starting in " + delayInMs + " ms.");
//    Task.Delay(delayInMs).Wait();
//    Console.WriteLine("Started Playing");
//}
//else if (startTime >= timeNow)
//{
//    var delayInMs = (int)timeDifference.TotalMilliseconds;
//
//    Console.WriteLine("Starting in " + delayInMs + " ms.");
//    Task.Delay(delayInMs).Wait();
//    Console.WriteLine("Started Playing");
//}
using System.Runtime;
using ByteDev.Sonos;
using ByteDev.Sonos.Device;
using ByteDev.Sonos.Models;
using ByteDev.Sonos.Upnp.Services.Models;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
SonosDevice test = new SonosDevice("10.1.12.251");

string ip = "10.1.12.251";

Console.WriteLine(test.HardwareVersion);
Console.WriteLine(test.Udn);
Console.WriteLine(test.SoftwareVersion);
Console.WriteLine(test.IpAddress);
Console.WriteLine(test.ModelName);
Console.WriteLine(test.ModelDescription);
Console.WriteLine(test.FriendlyName);
Console.WriteLine(test.SoftwareVersion);


SonosController controller = new SonosControllerFactory().Create("10.1.12.251");


//controller.PauseAsync().Wait();

UnitOfWork _uow = new();

//await _uow.ISonosConnectorRepo.SetTuneInStationAsync("10.1.12.251", "stream.rockantenne.bayern/80er-rock/stream/mp3");
//await _uow.ISonosConnectorRepo.SetTuneInStationAsync("10.1.12.251", "web.radio.antennevorarlberg.at/av-live/stream/mp3");

//await _uow.ISonosConnectorRepo.StopPlaying("10.1.12.251");
await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync("10.1.12.251", "https://open.spotify.com/playlist/37i9dQZEVXbMDoHDwVN2tF");

await _uow.ISonosConnectorRepo.StartPlaying(ip);
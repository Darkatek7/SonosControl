var startTime = new TimeOnly(6, 0);
var stopTime = new TimeOnly(17, 0);
var timeNow = new TimeOnly(17, 1);

var timeDifference = startTime - timeNow;

if (timeNow >= stopTime || startTime >= timeNow)
{
    var delayInMs = (int)timeDifference.TotalMilliseconds;

    Console.WriteLine("Starting in " + delayInMs + " ms.");
    Task.Delay(delayInMs).Wait();
    Console.WriteLine("Started Playing");
}
else if (startTime >= timeNow)
{
    var delayInMs = (int)timeDifference.TotalMilliseconds;

    Console.WriteLine("Starting in " + delayInMs + " ms.");
    Task.Delay(delayInMs).Wait();
    Console.WriteLine("Started Playing");
}
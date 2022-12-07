var startTime = new TimeOnly(6, 0);
var stopTime = new TimeOnly(13, 50);

TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
var timeDifference = startTime - timeNow;
var timeDifference2 = stopTime - timeNow;


Console.WriteLine("Time Now: " +timeNow.ToString());
Console.WriteLine("Time Until Morning: " + timeDifference.ToString());
Console.WriteLine("Time Until Evening: " + timeDifference2.ToString());
Console.WriteLine("Time Until Evening: " + timeDifference2.TotalSeconds.ToString());
var t = int.Parse(timeDifference2.TotalMilliseconds.ToString().Substring(0, timeDifference2.TotalMilliseconds.ToString().IndexOf(",") + 1).Replace(",", ""));
Console.WriteLine(t);
Console.WriteLine(t - 10);

if (stopTime <= timeNow)
{
    Console.WriteLine(timeDifference2);
}
else
{
    var delayInMs = (int)timeDifference2.TotalMilliseconds;

    Console.WriteLine(timeDifference2);
    Console.WriteLine(delayInMs);
}
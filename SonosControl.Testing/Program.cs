var startTime = new TimeOnly(6, 0);
var stopTime = new TimeOnly(18, 0);

TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
var timeDifference = startTime - timeNow;
var timeDifference2 = stopTime - timeNow;


Console.WriteLine("Time Now: " +timeNow.ToString());
Console.WriteLine("Time Until Morning: " + timeDifference.ToString());
Console.WriteLine("Time Until Evening: " + timeDifference2.ToString());

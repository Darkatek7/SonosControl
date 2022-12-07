var startTime = new TimeOnly(6, 0);
var stopTime = new TimeOnly(13, 50);

TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
var timeDifference = new TimeOnly(23, 59, 59, 999, 999) - timeNow;
var delay = (int)timeDifference.TotalMilliseconds;
Console.WriteLine("Waiting until next Day before Playing again");
Console.WriteLine(delay.ToString());
# SonosControl
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

https://hub.docker.com/r/darkatek7/sonoscontrol
____

## Description
This Self hosted application is used to automate the process of turning on/off a Sonos Speaker daily using on a Start/Stop Time (UTC) set in the config file or the UI.
When using my Docker Image the application doesn't start the Sonos Speaker on Weekends and on Austrian holidays. I will add an option to change this behavior in the UI in near future. For now you have to manually adjust the code.
____

## Info
* Blazor Server Application (.Net 7)
* Docker
* Uses UTC time
____

## Mobile View
![Screenshot 2022-12-14 084036](https://user-images.githubusercontent.com/50767771/207535102-0176c295-8d4f-49ca-90a4-a48dd46d42f9.png)

____
## Usage
### Docker Compose ([click here for more info](https://docs.linuxserver.io/general/docker-compose))

Create a file called docker-compose.yml.

Ubuntu/Debian:
```
nano docker-compose.yml
```

Paste following code:
```
---
version: '3.4'
services:
  sonos:
    container_name: sonos
    image: darkatek7/sonoscontrol:latest        # docker image
    ports:
      - 80:80                                   # port used for localhost ip
    restart: unless-stopped                     # restart policy
    environment:
      - TZ:"Europe/Vienna"
    volumes:
      - ./Data:/app/Data
```

### *This is optional. You only have to do this if you get a config.json error for some reason.*

At first create a folder called **Data**.

Ubuntu/Debian:
```
mkdir Data
```

Now create a file called **config.json**

Ubuntu/Debian:
```
nano Data/config.json
```
and paste the following:
```
{
    "Volume": 14,
    "StartTime": "06:00:00",
    "StopTime": "18:00:00",
    "IP_Adress": "10.0.0.1"
}
```
You can change the config to fit your needs or change it later in the UI.


## TODO
* Add option to change the days which the automation should run
* Add option to change the holiday location
* Create a nice UI (Currently I am only using a minimalistic UI that only displays nicely on mobile)

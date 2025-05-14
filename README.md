# SonosControl
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

https://hub.docker.com/r/darkatek7/sonoscontrol
____

## Description
This Self hosted application is used to automate the process of turning on/off a Sonos Speaker daily using on a Start/Stop Time (UTC) set in the config file or the UI.
The App also supports playing predefined TuneIn Stations ord Spotifiy Songs/Playlists/Albums.
____

## Info
* Blazor Server Application (.Net 9)
* Docker
____

## Mobile View
![image](https://github.com/user-attachments/assets/08561ba0-3198-44a4-8074-3f5bf7858116)


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

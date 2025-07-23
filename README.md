# SonosControl
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

https://hub.docker.com/r/darkatek7/sonoscontrol
____

## Description
This Self hosted application is used to automate the process of turning on/off a Sonos Speaker daily using on a Start/Stop Time (UTC) set in the config file or the UI.
The App also supports playing predefined TuneIn Stations ord Spotifiy Songs/Playlists/Albums.
____

## What's New
### Version July 2025
- ✅ **User Authentication Added**  
  You can now register and log in to the app using a simple authentication system.  
  ➤ Visit [`/register`](http://localhost:8080/register) to create new user accounts.

- 🛠 Improved UI and Station Lookup
- 🔧 Minor bug fixes and backend optimizations

> Want to see what’s coming next? Check the [TODO section](#todo) below.

___

## Info
* Blazor Server Application (.Net 9)
* Docker
____

## UI
### Index
![image](https://github.com/user-attachments/assets/91de85bb-c1ca-434d-9480-57eb02fd9ea6)

### Config
![image](https://github.com/user-attachments/assets/88e804df-c6a1-458e-8ed3-0f1ddef2d4ee)

### Station Lookup
![image](https://github.com/user-attachments/assets/263a3161-c104-4060-88b5-89e7f93f7066)
![image](https://github.com/user-attachments/assets/e3a156f9-2af2-4fdf-96f0-07c78efc4f4f)

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
      - 8080:8080                               # port used for localhost ip
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

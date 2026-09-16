FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src
COPY . .
RUN dotnet restore Dragonmind.Knowledge.sln
CMD ["dotnet","test","Dragonmind.Knowledge.sln","-c","Release","--no-restore"]

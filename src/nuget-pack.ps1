# Clean and build in release
dotnet restore
dotnet clean
dotnet build -c Release

# Create all NuGet packages
dotnet pack Aero.Actors/Aero.Actors.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Auth/Aero.Auth.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Caching/Aero.Caching.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Core.Ai/Aero.Core.Ai.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Core/Aero.Core.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Elastic/Aero.Elastic.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Events/Aero.Events.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.MartenDB/Aero.MartenDB.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.MerakiUI/Aero.MerakiUI.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Models/Aero.Models.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.EfCore/Aero.EfCore.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Marten/Aero.Marten.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Services/Aero.Services.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.SignalR/Aero.SignalR.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Social/Aero.Social.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Validators/Aero.Validators.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Web/Aero.Web.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Cloudflare/Aero.Cloudflare.csproj --no-build -c Release -o ./artifacts
dotnet pack Aero.Social/Twitter.Client/Aero.Social.Twitter.Client.csproj --no-build -c Release -o ./artifacts

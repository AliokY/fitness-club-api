# Fitness Club API

Requirements: .NET 9 SDK and Docker or Podman with Compose support.

## Run locally

For local development, store a random JWT signing key of at least 32 bytes outside
the project with .NET User Secrets. In PowerShell:

```powershell
$fitnessClubBytes = New-Object byte[] 32
$fitnessClubRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $fitnessClubRng.GetBytes($fitnessClubBytes) } finally { $fitnessClubRng.Dispose() }
$fitnessClubJwt = [Convert]::ToBase64String($fitnessClubBytes)
dotnet user-secrets set --project WebApi "Jwt:Key" $fitnessClubJwt | Out-Null
Remove-Variable fitnessClubJwt, fitnessClubBytes, fitnessClubRng
```

The `WebApi` launch profile uses the Development environment, where User Secrets
are loaded automatically. If no local key is configured, the application creates
a temporary random key in memory. Previously issued tokens become invalid after
the application restarts. For production, set `Jwt__Key` in the process
environment or a secrets manager. Never commit the key to Git. All instances of
the same deployment must use the same key.

Start PostgreSQL and Redis, then run the API. If you use Podman, replace
`docker compose` with `podman compose` in the commands below:

```sh
docker compose up -d
dotnet run --project WebApi
```

The API listens on `http://localhost:5080`. Swagger is at
`http://localhost:5080/swagger`, and the health check is at
`http://localhost:5080/health`. Database migrations and seed data are applied
when the application starts.

Seeded class times are relative to the first startup. To create a fresh dataset,
recreate the database:

```sh
docker compose down -v
docker compose up -d
```

## Migrations

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project DataAccessLayer --startup-project WebApi
```

New migrations are applied the next time the application starts.

## Tests

```sh
dotnet test
```

Tests require PostgreSQL and Redis from `docker compose`. A separate test
database is created and removed after the run.

## Seeded accounts

All users have the password `Password123!`.

| Role | Logins |
|---|---|
| `user` | `user001@club.test` … `user200@club.test` |
| `admin` | `admin001@club.test` … `admin005@club.test` |

Sign in with `POST /auth/login` and a body such as
`{ "email": "user001@club.test", "password": "Password123!" }`. The response
contains `accessToken`.

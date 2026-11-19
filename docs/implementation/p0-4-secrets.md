# P0.4 — Keyring de secretos

**Estado:** completado y verificado (2026-11-17).  
**Cierre de la serie P0.** No crea migración EF: solo cambia la configuración y el formato de los valores cifrados ya persistidos.

## 1. Funcionalidades implementadas

### 1.1 Rotación del pepper de API keys

- Nueva variable `DOMIBASE_API_KEY_PEPPERS`: lista separada por comas. El **primero** es el pepper activo y firma las claves nuevas; el resto son peppers retirados que siguen verificando claves existentes.
- `DOMIBASE_API_KEY_PEPPER` (variable histórica) se mantiene como **pepper retirado**: se añade al final del keyring si no está ya incluido. Las instalaciones existentes siguen funcionando sin cambios.
- `IApiKeyGenerator` expone `ComputeVerificationHashes` (todos los hashes candidatos del valor) y `Verify` (comparación en tiempo constante contra cualquier pepper del keyring).
- `RuntimeProjectResolver` calcula todos los hashes en servidor y los compara en SQL con `hashes.Contains(...)`. Una clave emitida antes de la rotación sigue resolviendo; una clave emitida después no es verificable por el pepper antiguo.

### 1.2 Rotación de la clave AES de secretos en reposo

- Nueva variable `DOMIBASE_SECRETS_KEYS`: entradas `id:base64-key` separadas por comas. La **primera** cifra los secretos nuevos; las demás solo descifran. El `id` admite letras, dígitos, `-` y `_` (máximo 32 caracteres).
- Formato nuevo: `v2.<key-id>.<base64url(nonce | tag | ciphertext)>`. El identificador viaja con el valor, así que el descifrado elige la clave correcta sin probar todas.
- El formato `v1.<base64url(...)>` histórico sigue siendo soluble con `DOMIBASE_SECRETS_KEY`, que se conserva exclusivamente como clave legacy para los valores ya guardados en `domibase_control`.
- No se re-cifran masivamente las credenciales antiguas: se descifran con su clave de origen y, cuando se vuelven a escribir (por ejemplo al rotar credenciales de runtime), pasan a `v2` con la clave activa.

## 2. Arquitectura modificada

- `ApiKeyGeneratorOptions.AdditionalPeppers` y `ApiKeyGenerator` gestionan el keyring de pepper en la capa de Application, sin conocer configuración.
- `ControlPlaneServiceCollectionExtensions.ReadPeppers` normaliza ambas variables, valida longitud mínima (16 caracteres) y unicidad, y falla en arranque si no hay pepper válido.
- `SecretProtectionOptions` pasa de una única clave a `Key` activa + `ActiveKeyId` + `AdditionalKeys` + `LegacyKey`.
- `AesGcmSecretProtector` selecciona la clave por versión/identificador, valida integridad AES-GCM y rechaza valores malformados, identificadores desconocidos y manipulaciones sin filtrar el motivo.
- `InfrastructureServiceCollectionExtensions.ReadSecretProtectionOptions` parsea y valida el keyring AES; sigue aceptando instalaciones con solo `DOMIBASE_SECRETS_KEY`.
- El middleware administrativo, el resolve de Runtime y los contratos HTTP no cambian: la rotación es transparente para el SDK y para el panel.

## 3. Archivos creados

- `backend/tests/DomiBase.UnitTests/ApiKeyGeneratorKeyringTests.cs`
- `backend/tests/DomiBase.UnitTests/SecretProtectionKeyringTests.cs`
- `backend/tests/DomiBase.IntegrationTests/SecretKeyringRotationTests.cs`
- `docs/implementation/p0-4-secrets.md`

## 4. Archivos modificados

- `backend/src/DomiBase.Application/ApiKeys/IApiKeyGenerator.cs`
- `backend/src/DomiBase.Application/ApiKeys/ApiKeyGenerator.cs`
- `backend/src/DomiBase.Application/ApiKeys/ApiKeyGeneratorOptions.cs`
- `backend/src/DomiBase.Api/ControlPlane/ControlPlaneServiceCollectionExtensions.cs`
- `backend/src/DomiBase.Infrastructure/Security/SecretProtectionOptions.cs`
- `backend/src/DomiBase.Infrastructure/Security/AesGcmSecretProtector.cs`
- `backend/src/DomiBase.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
- `backend/src/DomiBase.Infrastructure/Runtime/RuntimeProjectResolver.cs`
- `backend/tests/DomiBase.IntegrationTests/DomiBaseWebApplicationFactory.cs` (permite simular secretos rotados mediante variables de entorno y las restaura al disponer)
- `docs/implementation/roadmap.md`
- `README.md`
- `docs/architecture/overview.md`
- `.env.example`

## 5. Migraciones creadas

Ninguna. El keyring se resuelve íntegramente desde configuración; los valores `v1` ya almacenados siguen siendo válidos.

## 6. Pruebas ejecutadas

- `dotnet build backend/DomiBase.sln`: 0 errores, 0 warnings.
- `dotnet test backend/DomiBase.sln`: **213/213** en verde.
  - UnitTests: 125 (9 nuevos de keyring: 5 AES + 4 pepper).
  - IntegrationTests: 77 (1 nuevo end-to-end de rotación de ambos secretos).
  - SecurityTests: 11 (sin cambios).

Casos nuevos relevantes: cifrado `v2` con ID de clave activo, descifrado con clave retirada, compatibilidad `v1` solo con clave legacy, rechazo de ID desconocido/manipulación/valores malformados, validación de longitudes e IDs duplicados, hash con pepper activo, verificación con peppers antiguos y nuevo, rechazo de claves débiles/duplicadas, y prueba end-to-end donde un proyecto creado antes de la rotación sigue sirviendo datos con el pepper anterior y la clave AES anterior, mientras las claves nuevas usan los secretos rotados.

## 7. Procedimiento de rotación (operativo)

1. Generar un pepper nuevo y una clave AES-256 nueva fuera del repositorio.
2. Anteponerlos en las variables de entorno del host:
   - `DOMIBASE_API_KEY_PEPPERS=<pepper-nuevo>,<pepper-actual>,...`
   - `DOMIBASE_SECRETS_KEYS=rot-<id>:<base64-nueva>,ant-<id>:<base64-actual>,...`
3. Conservar `DOMIBASE_API_KEY_PEPPER` y `DOMIBASE_SECRETS_KEY` con los valores anteriores mientras existan registros `v1` o claves emitidas con el pepper antiguo.
4. Reiniciar la API y verificar: clave antigua sigue resolviendo, clave nueva se emite con el pepper activo, proyecto existente sigue arrancando (su contraseña `v1` se descifra con la clave legacy).
5. Cuando ya no queden valores antiguos, retirar las entradas correspondientes del keyring y, solo entonces, eliminar las variables históricas.

**Backup:** las claves y peppers son secretos de despliegue y no se guardan en la base ni en Git. Deben respaldarse en el gestor de secretos del homelab antes de cualquier rotación; perder el keyring antiguo hace irrecuperables las credenciales cifradas con esas claves.

## 8. Resultados

P0.4 cierra la serie P0: el pepper de API keys y la clave de cifrado de secretos pueden rotarse sin interrumpir proyectos ni claves existentes, con verificación en tiempo constante y sin exponer secretos en logs, respuestas HTTP ni contratos públicos.

## 9. Riesgos identificados

- El keyring se lee de variables de entorno al arrancar: un cambio requiere reinicio del host. No hay recarga en caliente.
- La rotación AES es diferida: los secretos antiguos no se re-cifran automáticamente, así que la clave legacy debe conservarse mientras queden valores `v1`.
- Las variables de entorno del proceso son visibles para el usuario que ejecuta el servicio; en el despliegue conviene inyectarlas desde el gestor de secretos de Portainer y restringir el acceso al host.
- No hay límite superior en el número de entradas del keyring; una lista muy larga aumenta el coste de verificación de pepper por petición.

## 10. Limitaciones pendientes

- No hay UI para gestionar keyrings: se configuran en el entorno del servidor.
- No hay comando administrativo de re-cifrado masivo ni de eliminación de entradas retiradas.
- Las fases P1–P10 de la propuesta BaaS se planificarán individualmente; no se marcan como completadas por existir documentación.

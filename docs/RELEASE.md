# Gestione delle release

## Numerazione
Versione a calendario **`AAAA.MM.N`**: anno, mese, progressivo del rilascio nel mese (riparte da 1 a ogni mese). Esempi: `2026.10.1`, `2026.10.2`, `2026.11.1`.
Non esiste un numero "major/minor": ogni rilascio dichiara cosa cambia nel `CHANGELOG.md`. Le correzioni urgenti sono un nuovo `N`.

## Fonte unica
Il numero è scritto **solo** in `Directory.Build.props` (`<VhaVersion>`). Da lì deriva la versione di tutti gli assembly (`.exe`/`.dll`), il file `VERSION.txt` e il nome del pacchetto `VhaSetup-<versione>.zip`.
Si controlla con `ClusteredService.exe --version`, `CsuCmd.exe --version`, `VhaSetup.exe --version`, `ClusteredService.exe --check Jelena-<AG>`, e nell'intestazione dei log.

## Procedura
1. Aggiorna `<VhaVersion>` in `Directory.Build.props`.
2. Aggiungi in cima a `CHANGELOG.md` la voce `## [AAAA.MM.N] - AAAA-MM-GG` con le modifiche.
3. `dotnet test` (tutti i progetti; con Docker per i test di integrazione) deve passare.
4. `pwsh installer/Build-Package.ps1 -IncludePrerequisites` (rifiuta di costruire se versione e CHANGELOG non coincidono).
5. Commit, poi tag annotato `vAAAA.MM.N` e push del tag: `git tag -a v2026.10.1 -m "vHA 2026.10.1" && git push origin v2026.10.1`.
6. Il workflow CI su tag `v*` costruisce il pacchetto e lo allega come artefatto; verifica che il tag coincida con `Directory.Build.props`.

## Aggiornare un ambiente
Installa lo stesso pacchetto su tutti i nodi di un gruppo, un nodo per volta (prima il passivo). Le versioni di `Vha.Core.dll` su nodi diversi non devono restare diverse a lungo: la replica CSU tra i nodi usa solo tabelle e SCN, non un protocollo versionato, ma è buona norma mantenerli allineati.

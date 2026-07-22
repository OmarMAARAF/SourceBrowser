# Indexation croisée d'un `.binlog` Linux → Windows : problèmes rencontrés et solutions

Ce document retrace, dans l'ordre chronologique, tous les problèmes rencontrés lors de l'intégration de la fonctionnalité `/rebase` dans SourceBrowser, ainsi que la façon dont chacun a été résolu.

Le cas d'usage concret : les **builds** sont exécutés sur des agents Linux TeamCity ; le fichier `compilation.binlog` produit est ensuite copié sur une machine Windows où SourceBrowser doit générer le site d'indexation.

---

## Contexte : qu'est-ce qu'un `.binlog` ?

MSBuild dispose d'un format de journal binaire (`.binlog`) qui enregistre l'intégralité d'une compilation : projets ouverts, arguments de ligne de commande passés aux compilateurs Roslyn (`csc`/`vbc`), chemins de tous les fichiers source et de toutes les références (assemblies NuGet, SDK…).

SourceBrowser exploite ce journal pour reconstituer le graphe de compilation sans avoir à relancer MSBuild. Il extrait les **invocations du compilateur** — chaque invocation décrit un projet compilé — puis les rejoue via Roslyn pour analyser et indexer les sources.

Le problème fondamental est que tous les chemins inscrits dans le binlog sont ceux de la machine qui a lancé le build (Linux). Lorsque SourceBrowser tourne sur Windows, aucun de ces chemins n'existe localement.

---

## Le lecteur binlog et ses deux modes

SourceBrowser embarque deux stratégies pour lire un `.binlog` :

### Mode 1 — Streaming reader (lecteur événementiel)

`Microsoft.Build.Logging.StructuredLogger.BinLogReader` rejoue le journal en émettant des événements MSBuild (`TargetStarted`, `MessageRaised`…). Le code accroche ces événements pour capturer les commandes `Csc`/`Vbc` et en extraire les invocations.

**Avantage :** rapide, faible consommation mémoire.  
**Inconvénient :** repose sur la correspondance exacte entre la version de MSBuild qui a produit le binlog et celle embarquée dans SourceBrowser. Si la version est plus récente, certains types d'événements ne sont pas reconnus et aucune invocation n'est remontée.

### Mode 2 — Structured tree reader (lecteur arborescent)

`Microsoft.Build.Logging.StructuredLogger.Serialization.Read()` désérialise le binlog en une arborescence d'objets (Build → Project → Target → Task…). Le code parcourt l'arbre à la recherche des nœuds `Task` nommés `Csc` ou `Vbc`, fils d'une cible `CoreCompile`, et lit leur propriété `CommandLineArguments`.

**Avantage :** indépendant de la version ; tant que la structure logique du binlog contient un nœud `Csc`/`Vbc`, l'invocation est trouvée.  
**Inconvénient :** légèrement plus lent (désérialisation complète en mémoire).

### Le mécanisme de repli (*fallback*)

Dans `BinLogReader.cs`, le streaming reader est toujours tenté en premier. Si la liste d'invocations retournée est vide (zéro invocations trouvées), SourceBrowser logue :

```
No compiler invocations found via the streaming binlog reader for '...'.
Falling back to the structured tree reader.
```

puis appelle `ExtractInvocationsFromBuild()` (le lecteur arborescent). Cela explique le message visible dans tous les journaux d'exécution : ce n'est pas une erreur, c'est la séquence normale quand le binlog a été produit par une version plus récente de MSBuild.

---

## Problème 1 — `ArgumentNullException: format` et streaming reader silencieux

### Symptôme

```
System.ArgumentNullException: Value cannot be null.
Parameter name: format
   at System.String.FormatHelper(IFormatProvider provider, String format, ParamsArray args)

No compiler invocations found via the streaming binlog reader for '...'. 
Falling back to the structured tree reader.
```

### Cause

L'exception `format = null` provient d'un appel interne au streaming reader lorsqu'il tente de formatter un message de log pour un type d'événement qu'il ne connaît pas (version de binlog trop récente). Elle est capturée en tant que *first-chance exception*, loguée, et le streaming reader continue — mais il n'a rien capturé.

### Pourquoi le fallback a aidé

Le structured tree reader ignore complètement le flux d'événements. Il lit l'arborescence directement depuis les blocs binaires du fichier. Les nœuds `Csc`/`Vbc` sont présents quelle que soit la version de MSBuild, car ils font partie du schéma logique du projet. Le fallback a donc permis d'extraire toutes les invocations correctement.

### Pourquoi ça n'a pas fonctionné dès le premier essai

La première exécution avait été tentée sans `/rebase`. Le fallback a réussi à extraire les invocations, mais les chemins dans celles-ci étaient toujours des chemins Linux (`/opt/buildagent/work/rdws/...`). Les erreurs suivantes ont commencé à apparaître car Roslyn ne pouvait pas ouvrir ces chemins sur Windows.

---

## Problème 2 — `ArgumentException: Chemin d'accès absolu attendu` (XmlFileResolver)

### Symptôme

```
System.ArgumentException: Chemin d'accès absolu attendu.
Nom du paramètre : baseDirectory
   at Microsoft.CodeAnalysis.XmlFileResolver..ctor(String baseDirectory)
```

La valeur passée à `XmlFileResolver` était un chemin Linux : `/opt/buildagent/work/rdws/...`.

### Cause

L'option `/rebase` existait déjà dans le code, mais la détection de l'ancien préfixe (`oldRoot`) était calculée comme le plus long préfixe commun de **tous** les chemins de l'invocation, y compris `OutputAssemblyPath`. Or, `OutputAssemblyPath` est calculé par le parseur Roslyn à la lecture du binlog. Sur Windows, Roslyn convertit automatiquement le chemin Linux absolu `/opt/...` en `C:\opt\...` (il interprète `/opt` comme un chemin relatif à la racine du lecteur courant). On se retrouvait donc avec deux styles incompatibles :

- `ProjectFilePath` → `/opt/buildagent/work/rdws/.../Rdws.csproj` (chemin Linux, avec `/`)
- `OutputAssemblyPath` → `C:\opt\buildagent\work\rdws\...\Ard.Rdws.dll` (chemin Windows, avec `\`)

Le calcul du plus long préfixe commun compare les segments un par un. Le premier segment de `/opt/...` est `""` (avant le premier `/`), celui de `C:\opt\...` est `"C:"` — ils diffèrent immédiatement. Le préfixe commun est donc `null` et le rebasage **ne fait rien**.

### Solution

`RebaseInvocations` utilise désormais uniquement les `ProjectFilePath` (style cohérent, directement issus du binlog) pour détecter `oldRoot`. `OutputAssemblyPath` est ensuite recalculé à partir de la ligne de commande rebasée plutôt que simplement préfixé.

---

## Problème 3 — `Document doesn't exist on disk` (multi-racines VCS)

### Symptôme

```
Document doesn't exist on disk: D:\TeamCity\SRVCLDGUSD916-2\work\Prod\Data\ComputeData.cs
```

Le fichier existait bien, mais à l'emplacement :
```
D:\TeamCity\SRVCLDGUSD916-2\work\RdwsSourceBrowser\rdws-core-api\Prod\Data\ComputeData.cs
```

### Cause

En TeamCity, chaque racine VCS est extraite dans un sous-dossier distinct sous le répertoire de travail de la build SourceBrowser :

```
RdwsSourceBrowser\
    rdws-core-api\   ← VCS root 1
    rdws-model\      ← VCS root 2
    rdws\            ← VCS root 3
    rdws-worker\     ← VCS root 4
```

Mais sur l'agent de build Linux, les sources étaient directement sous `work/` :

```
/opt/buildagent/work/rdws/...
/opt/buildagent/work/Prod/...
```

Le rebasage remplaçait `/opt/buildagent/work` par `D:\...\RdwsSourceBrowser`, sans descendre dans le bon sous-dossier.

### Solution

`RebaseInvocations` sonde désormais le **système de fichiers réel** : pour chaque invocation, il cherche le fichier projet enregistré dans le binlog en testant le répertoire de rebase **et chacun de ses sous-dossiers immédiats** avec le plus long suffixe de chemin possible. Quand le fichier est trouvé, il déduit automatiquement l'ancien préfixe ET le bon sous-dossier local. Un seul `/rebase:D:\...\RdwsSourceBrowser` suffit pour tous les binlogs.

---

## Problème 4 — Séparateurs mixtes dans la ligne de commande

### Symptôme

```
System.ArgumentException: Can't resolve metadata reference: 
'D:\TeamCity\SRVCLDGUSD916-2\work\RdwsSourceBrowser/rdws/.ard/obj/...'
```

Le chemin commence avec `\` (Windows) puis bascule en `/` (Linux) — Roslyn rejette ce mélange.

### Cause

La réécriture de la ligne de commande remplace l'ancien préfixe par `newRoot`. `newRoot` utilise des `\` (Windows), mais **le reste du chemin** n'est pas touché et conserve les `/` du binlog Linux. La concaténation produit un chemin hybride invalide.

### Solution

Avant la réécriture de `CommandLineArguments`, le code détecte le séparateur utilisé par le binlog source (présence de `\` dans `oldRoot` → séparateur Windows, sinon `/` → séparateur Linux). `newRoot` est ensuite converti dans ce même style avant d'être injecté. Les chemins dans la ligne de commande restent donc homogènes : tous en `/` pour un binlog Linux, tous en `\` pour un binlog Windows.

---

## Problème 5 — Références NuGet/SDK non rebasables

### Symptôme

```
System.ArgumentException: Can't resolve metadata reference: 
'/opt/buildagent/system/dotnet/.nuget/autofac/9.0.0/lib/net10.0/Autofac.dll'
```

### Cause

Les assemblies NuGet et SDK sont stockées **en dehors du dépôt**, dans un cache géré par le build agent Linux (`/opt/buildagent/system/dotnet/.nuget/...`). Ces chemins ne peuvent pas être rebasés car leur emplacement Windows équivalent n'existe pas du tout sur la machine d'indexation. Roslyn appelle `CommandLineProject.CreateProjectInfo`, rencontre cette référence, tente de l'ouvrir, échoue, et lève une exception qui **avorte l'indexation du projet entier** — avant même que le filtre `RemoveNonExistingReferences` de SourceBrowser ait pu s'exécuter.

### Solution

Un nouveau prétraitement dans `SolutionGenerator.CreateSolution` analyse la ligne de commande avec une expression régulière avant de la passer à Roslyn. Il supprime chaque switch `/reference`, `/link` et `/analyzer` dont le fichier cible n'existe pas localement. Les références qui existent (celles qui ont été correctement rebasées et se trouvent bien sur le disque local) sont conservées. Roslyn reçoit donc une ligne de commande expurgée des références introuvables et ne lève plus d'exception. Les types provenant de ces assemblies disparues restent navigables via les fédérations hors-ligne configurées dans le site SourceBrowser.

---

## Récapitulatif chronologique

| # | Erreur | Cause | Solution |
|---|--------|-------|----------|
| 1 | `ArgumentNullException: format` + 0 invocations | Binlog version trop récente pour le streaming reader | Fallback automatique vers le structured tree reader |
| 2 | `Chemin d'accès absolu attendu` (XmlFileResolver) | Préfixe commun calculé sur des styles de chemin mixtes (`/opt/...` vs `C:\opt\...`) → rebasage silencieux | Détecter `oldRoot` uniquement depuis `ProjectFilePath` ; recalculer `OutputAssemblyPath` depuis la ligne de commande rebasée |
| 3 | `Document doesn't exist on disk` | `/rebase` pointait le dossier parent mais les sources étaient dans des sous-dossiers VCS distincts | Sondage du système de fichiers : probing de chaque sous-dossier immédiat pour localiser le bon checkout |
| 4 | Séparateurs mixtes `\` + `/` dans la ligne de commande | `newRoot` en backslash concaténé avec reste du chemin Linux en slash | Émettre `newRoot` dans le même style de séparateur que les chemins du binlog |
| 5 | `Can't resolve metadata reference: /opt/buildagent/...` | Références NuGet/SDK Linux hors-dépôt, non rebasables, rejetées par Roslyn avant le filtre SourceBrowser | Supprimer les références introuvables de la ligne de commande avant l'appel à `CreateProjectInfo` |

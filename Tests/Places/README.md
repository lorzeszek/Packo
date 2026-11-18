# Places checks

Run `dotnet run --project Tests/Places/Places.csproj` for the local SQLite, view-model,
offline/cancellation and source/AI-response validation checks. No live accounts or paid API calls are used.

Append `-- --live` to also read Open-Meteo, Wikipedia and Wikidata for Warsaw using
a local AI stub. Append `-- --live-ai` instead to exercise the full pipeline with the
existing `OpenAI` configuration from the ignored root `appsettings.json` (one paid AI request).

The first version keeps each trip's suggestions and personal saved list on the device,
separately for each local account. Discovery requires an internet connection; saved
places, notes and visited status work offline. Suggestions cover a 10 km radius around
the destination's geocoded centre. City destinations work best. Existing saved places
are retained when trip details or the application language change.

Place identifiers, coordinates and article links come from Wikipedia's MediaWiki GeoSearch;
localized names, short source facts and optional official websites come from Wikidata.
AI only selects existing identifiers and writes short descriptions from those facts.
Photos and cloud synchronization of saved places are outside this first version.

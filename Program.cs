﻿using Indx.Api;
using Indx.Json;
using Indx.Json.Api;
using Indx.JsonHelper;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace IndxConsoleApp
{
    internal class Program
    {
        private static void Main()
        {
            // 
            // INITIALIZATION & DATA LOAD
            // 

            // Create search engine instance
            var engine = new SearchEngineJson();
            // Load a license like this: new SearchEngineJson("file.license");
            // Get a developer license on https://indx.co

            // Display header
            AnsiConsole.Write(
                new FigletText("indx " + new Version(engine.Status.Version).ToString(2))
                    .Centered());

            // Dataset
            var fileName = "pokedex_extended";
            // Locate file (adjust relative path if needed)
            string file = "data/" + fileName + ".json";
            if (!File.Exists(file))
                file = "../../../" + file;

            // Set encoding for console
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            // Stream data from file and initialize engine
            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                AnsiConsole.Status()
                    .SpinnerStyle(Color.LightSlateBlue)
                    .Spinner(Spinner.Known.Line) // choose a spinner style
                    .Start($"Analyzing JSON", ctx =>
                    {
                        // Perform your loading operation.
                        engine.Init(fstream, out string _errorMessage);
                    });
            }
            if (engine.DocumentFields == null)
                return;
            PrintFields(false, engine.DocumentFields);

            // 
            // CONFIGURE FIELDS
            // 

            Field sortField = null!;

            engine.GetField("pokedex_number")!.Indexable = true;
            engine.GetField("pokedex_number")!.Weight = Weight.High;
            engine.GetField("pokedex_number")!.Filterable = true;

            engine.GetField("name")!.Indexable = true;
            engine.GetField("name")!.Weight = Weight.High;

            engine.GetField("type1")!.Indexable = true;
            engine.GetField("type1")!.Weight = Weight.Low;
            engine.GetField("type1")!.Facetable = true;

            engine.GetField("type2")!.Indexable = true;
            engine.GetField("type2")!.Weight = Weight.Low;
            engine.GetField("type2")!.Facetable = true;

            engine.GetField("classfication")!.Indexable = true;
            engine.GetField("classfication")!.Weight = Weight.Low;
            engine.GetField("classfication")!.Facetable = true;

            engine.GetField("is_legendary")!.Facetable = true;
            engine.GetField("is_legendary")!.Filterable = true;

            engine.GetField("attack")!.Sortable = true;

            engine.GetField("abilities")!.Facetable = true;

            sortField = engine.GetField("attack")!;


            // 
            // LOAD DATA FROM JSON
            // 

            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                DateTime loadStart = DateTime.Now;
                AnsiConsole.Status()
                    .SpinnerStyle(Color.LightSlateBlue)
                    .Spinner(Spinner.Known.BouncingBar) // choose a spinner style
                    .Start($"Loading {file}", ctx =>
                    {
                        // Perform your loading operation.
                        engine.LoadJson(fstream, out _);
                    });
                double loadTime = (DateTime.Now - loadStart).TotalMilliseconds;
                AnsiConsole.Markup($"\nLoading {file} completed in {(int)loadTime / 1000.0:F1} seconds\n");
            }

            // 
            // INDEXING
            // 

            if (!engine.Index())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No fields marked as indexable");
                return;
            }
            else
            {
                engine.Index();
            }

            DateTime indexStart = DateTime.Now;
            double indexTime = 0;
            AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn()
                        .CompletedStyle(Color.LightSlateBlue)
                        .RemainingStyle(Color.Grey15)
                        .FinishedStyle(Color.LightSlateBlue),
                    new PercentageColumn()
                        .CompletedStyle(Color.Default)
                )
                .Start(ctx =>
                {
                    var task = ctx.AddTask("Indexing", autoStart: false);
                    task.StartTask();
                    while (engine.Status.SystemState != SystemState.Ready)
                    {
                        task.Value = engine.Status.IndexProgressPercent;
                        Thread.Sleep(50);
                    }
                    task.Value = 100;
                    task.Description = "[bold]Complete[/]";
                });

            indexTime = (DateTime.Now - indexStart).TotalMilliseconds;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"🟢 Indexed '{file}' ({engine.Status.DocumentCount} documents) and ready to search in {indexTime / 1000.0:F1} seconds\n");
            Console.ResetColor();

            // 
            // SET UP FILTERS & BOOST
            // 

            Filter combinedFilters = null!;
            int docsBoosted = 0;
            var boosts = new List<Boost>();

            // FILTER
            Filter origFilter = engine.CreateRangeFilter("pokedex_number", 1, 151)!;
            combinedFilters = origFilter; // could combine additional filters here with & operator

            // BOOST
            Filter legendaryFilter = engine.CreateValueFilter("is_legendary", true)!;
            boosts.Add(engine.CreateBoost(legendaryFilter!, BoostStrength.Med));

            // 
            // WAIT FOR USER TO START SEARCHING
            // 
            Console.WriteLine("Press [SPACE] to start searching...");
            while (Console.ReadKey(intercept: true).Key != ConsoleKey.Spacebar) { }
            Console.Clear();

            // 
            // INTERACTIVE SEARCH (Live Display)
            // 

            // Set up initial query and state variables
            string text = string.Empty;
            int num = 5;
            var query = new JsonQuery(text, num);
            query.Boosts = boosts.ToArray(); // immutable
            docsBoosted = JsonQuery.DocumentsOfBoost(boosts);

            bool enableFilters = false;
            bool enableBoost = false;
            bool allowEmptySearch = false;
            bool printFacets = false;
            bool truncateList = true;
            bool sortList = false;
            bool deepSearch = false;
            bool measurePerformance = false;
            bool performanceMeasured = false;
            bool showLicenseInfo = false;
            int truncationIndex = 0;
            double latency = 0.0;
            long memoryUsed = 0;
            bool continuousMeasure = true;
            int currentFacetPage = 0;

            DateTime lastInputTime = DateTime.Now;

            AnsiConsole.Live(new Rows([]))
                .Start(ctx =>
                {
                    while (true)
                    {
                        // Process key input if available (non-blocking)
                        while (Console.KeyAvailable)
                        {
                            var keyInfo = Console.ReadKey(intercept: true);

                            // Always process ESC immediately.
                            if (keyInfo.Key == ConsoleKey.Escape)
                                return; // exit live loop

                            // If idle time is less than 2 seconds, process normal input.
                            if ((DateTime.Now - lastInputTime).TotalSeconds < 2)
                            {
                                if (keyInfo.Key == ConsoleKey.Backspace && text.Length > 0)
                                    text = text.Substring(0, text.Length - 1);
                                else if (keyInfo.Key == ConsoleKey.UpArrow)
                                    num++;
                                else if (keyInfo.Key == ConsoleKey.DownArrow)
                                    num = Math.Max(1, num - 1);
                                else if (!char.IsControl(keyInfo.KeyChar))
                                    text += keyInfo.KeyChar;
                            }
                            else
                            {
                                // Idle for 2+ seconds: allow toggle keys.
                                switch (keyInfo.Key)
                                {
                                    case ConsoleKey.C:
                                        text = "";
                                        currentFacetPage = 0;
                                        continue;
                                    case ConsoleKey.T when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        truncateList = !truncateList;
                                        continue;
                                    case ConsoleKey.F when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (combinedFilters != null)
                                            enableFilters = !enableFilters;
                                        continue;
                                    case ConsoleKey.P when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        printFacets = !printFacets;
                                        if (!printFacets) currentFacetPage = 0;
                                        continue;
                                    case ConsoleKey.B when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (docsBoosted > 0)
                                            enableBoost = !enableBoost;
                                        continue;
                                    case ConsoleKey.E when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        allowEmptySearch = !allowEmptySearch;
                                        continue;
                                    case ConsoleKey.M when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        measurePerformance = !measurePerformance;
                                        continue;
                                    case ConsoleKey.L when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        showLicenseInfo = !showLicenseInfo;
                                        continue;
                                    case ConsoleKey.S when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (sortField != null)
                                            sortList = !sortList;
                                        continue;
                                    case ConsoleKey.D when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        deepSearch = !deepSearch;
                                        continue;
                                    case ConsoleKey.LeftArrow when printFacets:
                                        currentFacetPage = Math.Max(0, currentFacetPage - 1);
                                        continue;
                                    case ConsoleKey.RightArrow when printFacets:
                                        currentFacetPage++;
                                        continue;
                                    default:
                                        // For non-toggle keys, process as normal input.
                                        if (keyInfo.Key == ConsoleKey.Backspace && text.Length > 0)
                                        {
                                            text = text.Substring(0, text.Length - 1);
                                            currentFacetPage = 0;
                                        }
                                        else if (keyInfo.Key == ConsoleKey.UpArrow)
                                            num++;
                                        else if (keyInfo.Key == ConsoleKey.DownArrow)
                                            num = Math.Max(1, num - 1);
                                        else if (!char.IsControl(keyInfo.KeyChar))
                                            text += keyInfo.KeyChar;
                                        break;
                                }
                            }
                            // Update last input time after processing any key.
                            lastInputTime = DateTime.Now;
                        } // end inner while

                        // Update query parameters
                        query.Text = text;
                        query.MaxNumberOfRecordsToReturn = num;
                        if (sortField != null)
                            query.SortBy = sortList ? sortField : null;
                            query.SortAscending = false;
                        query.Filter = enableFilters ? combinedFilters! : null!;
                        if (docsBoosted > 0)
                            query.EnableBoost = enableBoost;
                        if(deepSearch)
                        {
                            if(engine.Status.DocumentCount < 750000) 
                                query.CoverageDepth = engine.Status.DocumentCount;
                            else query.CoverageDepth = 7500000;
                        } else query.CoverageDepth = 500;
                        if(!truncateList) query.EnableCoverage = false;
                        else query.EnableCoverage = true;

                        // Build search results table
                        var table = new Table();
                        table.Border(TableBorder.Simple);
                        table.BorderColor(Color.Grey15);
                        table.Expand();

                        table.AddColumn("Name");
                        table.AddColumn("Pokedex #");
                        table.AddColumn("Types");
                        table.AddColumn("Classification");
                        table.AddColumn("Stats [Grey30](Attack, Health, Speed)[/]");
                        table.AddColumn("Score");
                        

                        //
                        // SEARCH
                        //

                        var jsonResult = engine.Search(query);
                        truncationIndex = jsonResult.TruncationIndex;

                        if (jsonResult != null)
                        {
                            for (int i = 0; i < jsonResult.Records.Length; i++)
                            {
                                var key = jsonResult.Records[i].DocumentKey;
                                var score = jsonResult.Records[i].Score;
                                string json = engine.GetJsonDataOfKey(key);

                                var pokenum = JsonHelper.GetFieldValue(json, "pokedex_number");
                                var name = JsonHelper.GetFieldValue(json, "name");
                                var type1 = JsonHelper.GetFieldValue(json, "type1");
                                var type2 = JsonHelper.GetFieldValue(json, "type2");
                                var classification = JsonHelper.GetFieldValue(json, "classfication");
                                var speed = JsonHelper.GetFieldValue(json, "speed");
                                var attack = JsonHelper.GetFieldValue(json, "attack");
                                var health = JsonHelper.GetFieldValue(json, "hp");
                                var legendary = JsonHelper.GetFieldValue(json, "is_legendary");
                                var legendarySymbol = legendary == "True" ? "🌟" : "";

                                var stats = new Table();
                                stats.Border(TableBorder.Rounded);
                                stats.BorderColor(Color.Grey30);
                                stats.HideHeaders();
                                stats.AddColumn("Attack");
                                stats.AddColumn("Health");
                                stats.AddColumn("Speed");
                                stats.AddRow(attack, health, speed);

                                table.AddRow(
                                    new Panel(new Markup($"{name} {legendarySymbol}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(new Markup(pokenum))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(new Markup($"{type1} {type2}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                        new Panel(new Markup(classification))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(stats)
                                        .Padding(new Padding(0))
                                        .Expand()
                                        .Border(BoxBorder.None),
                                    new Panel(new Markup($"{score}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0)
                                );
                            }
                        }

                        // Prepare header markup; escape dynamic text to avoid markup parsing errors.
                        string cursor = "█";
                        if ((DateTime.Now - lastInputTime).TotalSeconds >= 2) cursor = "";
                        var inputField = new Markup("🔍 Search: " + Markup.Escape(text) + cursor + "\n");

                        // Render list
                        var renderables = new List<IRenderable>
                        {
                            inputField,
                            table
                        };

                        // If idle for 2+ seconds and there is text (or empty search is allowed),
                        // add facets, performance info, and command instructions.
                        if ((DateTime.Now - lastInputTime).TotalSeconds >= 2 && (text.Length > 0 || allowEmptySearch))
                        {
                            query.EnableFacets = true;
                            var facetResult = engine.Search(query);
                            Dictionary<string, KeyValuePair<string, int>[]>? facets = facetResult.Facets;
                            Markup facetsMarkup = new Markup("");
                            if (printFacets && facets != null)
                            {
                                // Build a compact string for each facet group.
                                var facetGroups = new List<string>();
                                foreach (var field in engine.DocumentFields.GetFacetableFieldList())
                                {
                                    var fName = field.Name;
                                    var sb = new StringBuilder();
                                    sb.Append($"[bold]{Markup.Escape(fName)}[/]: ");
                                    if (facets.TryGetValue(fName, out var histogram) && histogram != null)
                                    {
                                        // Join key/value pairs with commas.
                                        var items = histogram.Select(item => $"{Markup.Escape(item.Key)} ({item.Value})");
                                        sb.Append(string.Join(", ", items));
                                    }
                                    facetGroups.Add(sb.ToString());
                                }

                                // Pagination: Show groupsPerPage facet groups per page.
                                int groupsPerPage = 2;
                                if(truncationIndex < 10) groupsPerPage = 4;
                                int totalPages = (int)Math.Ceiling((double)facetGroups.Count / groupsPerPage);
                                if (totalPages == 0)
                                    totalPages = 1;
                                // Ensure currentFacetPage is within bounds (this variable is updated when left/right arrow keys are pressed)
                                if (currentFacetPage >= totalPages)
                                    currentFacetPage = totalPages - 1;
                                if (currentFacetPage < 0)
                                    currentFacetPage = 0;

                                int start = currentFacetPage * groupsPerPage;
                                int count = Math.Min(groupsPerPage, facetGroups.Count - start);
                                var pageFacets = facetGroups.Skip(start).Take(count);
                                string facetText = string.Join("\n\n", pageFacets) +
                                                $"\n\n[grey]Page {currentFacetPage + 1} of {totalPages} [[LEFT/RIGHT]] to navigate)[/]";

                                facetsMarkup = new Markup(facetText);
                            }
    
                            if (!allowEmptySearch)
                                query.EnableFacets = false;

                            Markup additionalInfo = new Markup($"\nExact hits: {truncationIndex + 1} of {query.CoverageDepth} covered\n");
                            Markup performanceMeta = new Markup("");
                            if(printFacets) query.EnableFacets = true;


                            var grid = new Grid();
                            grid.AddColumn(new GridColumn());
                            grid.AddColumn(new GridColumn());
                            grid.AddColumn(new GridColumn());


                            var commands = new Table();
                            commands.Border(TableBorder.Rounded);
                            commands.BorderColor(Color.LightSlateBlue);
                            commands.AddColumn("Key");
                            commands.AddColumn("Command");
                            commands.AddColumn("Status");
                            commands.AddRow("[grey]SHIFT-[/]T", "[grey]Truncation[/]", truncateList ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]F", "[grey]Filters[/]", enableFilters ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]P", "[grey]Print facets[/]", printFacets ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]B", "[grey]Boosting[/]", enableBoost ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]E", "[grey]Empty search[/]", allowEmptySearch ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]M", "[grey]Measure performance[/]", measurePerformance ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]S", "[grey]Sorting[/]", sortList ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]D", "[grey]Deep search[/]", deepSearch ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");
                            commands.AddRow("[grey]SHIFT-[/]L", "[grey]Show license info[/]", showLicenseInfo ? "[cyan][[x]] Enabled[/]" : "[[ ]] Disabled");

                            
                            IRenderable performanceRenderable;
                            if (measurePerformance)
                            {
                                int numReps = 100;
                                if (!performanceMeasured || continuousMeasure)
                                {
                                    DateTime perfStart = DateTime.Now;
                                    Parallel.For(1, numReps, i => { engine.Search(query); });
                                    latency = (DateTime.Now - perfStart).TotalMilliseconds / numReps;
                                    memoryUsed = GC.GetTotalMemory(false) / 1024 / 1024;
                                }
                                var performanceTable = new Table();
                                performanceTable.Border(TableBorder.Rounded);
                                performanceTable.BorderColor(Color.Grey70);
                                performanceTable.AddColumn("Performance");
                                performanceTable.AddRow($"[grey]Response time:[/] {latency:F3} ms (avg of {numReps} reps)");
                                performanceTable.AddRow($"[grey]Memory used:[/] {memoryUsed} MB");
                                performanceTable.AddRow($"[grey]Document count:[/] {engine.Status.DocumentCount} / {engine.Status.LicenseInfo.DocumentLimit}");
                                performanceTable.AddRow($"[grey]Docs boosted:[/] {query.DocumentsBoosted}");
                                performanceTable.AddRow($"[grey]Version:[/] {engine.Status.Version}");
                                performanceTable.AddRow($"[grey]Valid License:[/] {engine.Status.LicenseInfo.ValidLicense}");
                                performanceRenderable = performanceTable;
                                performanceMeasured = true;
                            }
                            else
                            {
                                performanceRenderable = new Markup(""); // empty renderable when not measuring.
                                performanceMeasured = false;
                            }

                            IRenderable licenseRenderable;
                            if (showLicenseInfo)
                            {
                                var licenseTable = new Table();
                                licenseTable.Border(TableBorder.Rounded);
                                licenseTable.BorderColor(Color.Grey70);
                                licenseTable.AddColumn("License information");
                                licenseTable.AddRow($"[grey]Licensed to:[/] {engine.Status.LicenseInfo.LicensedTo}");
                                if(engine.Status.LicenseInfo.Licensed)
                                {
                                    licenseTable.AddRow($"[grey]Type:[/] {engine.Status.LicenseInfo.Type}");
                                    licenseTable.AddRow($"[grey]Description:[/] {engine.Status.LicenseInfo.Description}");
                                    licenseTable.AddRow($"[grey]Document limit:[/] {engine.Status.LicenseInfo.DocumentLimit}");
                                    licenseTable.AddRow($"[grey]Limit exceeded:[/] {engine.Status.LicenseInfo.DocumentLimitExceeded}");
                                    licenseTable.AddRow($"[grey]Valid License:[/] {engine.Status.LicenseInfo.ValidLicense}");
                                    licenseTable.AddRow($"[grey]Expires:[/] {engine.Status.LicenseInfo.ExpirationDate}");
                                }
                                licenseRenderable = licenseTable;
                            }
                            else
                            {
                                licenseRenderable = new Markup("");
                            }

                            grid.AddRow(commands, performanceRenderable, licenseRenderable); // left -> right panel

                            renderables.Add(facetsMarkup);
                            renderables.Add(additionalInfo);
                            renderables.Add(new Markup("[cyan]Press [[UP/DOWN]] to change num, [[ESC]] to quit, [[C]] to clear, or type to continue searching.[/]\n"));
                            renderables.Add(grid);
                        } // end 2s idle

                        var renderStack = new Rows(renderables); // combine renderables
                        ctx.UpdateTarget(renderStack);
                    } // end context
                }); // end Live view
                Thread.Sleep(50); // necessary for live view?
        } // end Main

        /// Prints detected JSON fields
        public static void PrintFields(bool printToDebugWindow, DocumentFields documentFields)
        {
            var fields = documentFields.GetFieldList();
            fields.Sort((x, y) => x.Name.CompareTo(y.Name));
            
            if (printToDebugWindow)
            {
                foreach (var field in fields)
                {
                    var printLine = $"{field.Name} ({field.Type}) \t {(field.IsArray ? "IsArray" : "")} \t{(field.Optional ? "Optional" : "")}";
                    System.Diagnostics.Debug.WriteLine(printLine);
                }
                return;
            }
            
            var table = new Table();
            table.BorderColor(Color.Grey50);
            table.Expand();
            table.Border = TableBorder.Horizontal;
            table.Title = new TableTitle("\n[LightSlateBlue]Detected JSON Fields[/]\n");
            
            // Add columns.
            table.AddColumn(new TableColumn("[bold]Field Name[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Type[/]").Centered());
            table.AddColumn(new TableColumn("[bold]Is Array?[/]").Centered());
            table.AddColumn(new TableColumn("[bold]Optional?[/]").Centered());
            
            // Add rows using Markup cells.
            foreach (var field in fields)
            {
                table.AddRow(
                    new Markup(field.Name),
                    new Markup(field.Type.ToString()),
                    new Markup(field.IsArray ? "Yes" : "No"),
                    new Markup(field.Optional ? "Yes" : "No")
                );
            }
            
            // Render the table.
            AnsiConsole.Write(table);
        } // end function PrintFields

    } // end class Program
} // end namespace
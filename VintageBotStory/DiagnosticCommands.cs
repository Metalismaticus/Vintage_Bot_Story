using VsBotKit;

namespace VintageBotStory;

/// <summary>
/// СТРАХОВКА «БОТ ЖИВЁТ В ВЫЖИВАНИИ» — ЧИСТОЕ ПРАВИЛО ОТЛАДОЧНЫХ КОМАНД.
///
/// Откуда взялась. Стенды отладки («!ямтест», «!блоктест») переключают бота в
/// творческий режим и возвращают обратно; упади прогон на середине — бот
/// остался бы летать. Поэтому при входе страховка проверяет режим.
///
/// ЧЬЯ ОНА. Не роли: страховку вешает <see cref="DiagnosticCommands.AddTo"/>,
/// а его зовут только под флагом «--diag» (Program.cs). Живьём это был прогон
/// роли «Выживальщик», и приписывать беду роли-торговцу значило бы отправить
/// следующего читать не тот файл.
///
/// ЖИВАЯ БЕДА, которую это правило чинит (журнал 16.08, 20:22:44 — и так в
/// КАЖДОМ прогоне): страховка слала «/player &lt;имя&gt; gamemode survival»
/// вслепую, сервер отвечал «у вас нет прав на использование этой команды» и
/// «For help, type /help player» — без имени команды и без последствий.
/// Человек по журналу не мог понять ни что спрошено, ни чего бот лишился.
///
/// Спрашивать было не о чем: свой режим игры и свои права сервер называет сам
/// (PlayerData(41), поля GameMode и Privileges — <see cref="SelfState.GameMode"/>
/// и <see cref="SelfState.May"/>). Право на эту команду — «gamemode»
/// (CmdPlayer: подкоманда gamemode требует Privilege.gamemode, сверено по
/// VintagestoryLib 1.22.7).
///
/// Правило чистое (правило 7 проекта): ни сети, ни времени — только факты от
/// сервера на входе. Проверяется <c>ModeInsuranceTests</c>.
/// </summary>
public static class ModeInsurance
{
    /// <summary>
    /// Что делать со страховкой при входе.
    /// </summary>
    /// <param name="Command">Команда серверу (null — не слать ничего).</param>
    /// <param name="Say">Что сказать хозяину (null — говорить не о чем).</param>
    public sealed record Verdict(string? Command, string? Say);

    /// <summary>
    /// Решить по фактам сервера.
    /// </summary>
    /// <param name="mode">Режим игры, как его назвал сервер (null — не назвал).</param>
    /// <param name="mayGamemode">
    /// Есть ли право «gamemode» (null — списка прав сервер не присылал).
    /// </param>
    /// <param name="name">Имя бота: команда «/player» требует имени игрока.</param>
    public static Verdict Decide(Vintagestory.API.Common.EnumGameMode? mode,
                                 bool? mayGamemode, string name)
    {
        string command = $"/player {name} gamemode survival";

        // Сервер режима не назвал — врать «всё в порядке» нельзя, но и слать
        // команду вслепую незачем: именно с этого начиналась беда
        if (mode == null)
            return new Verdict(null,
                "сервер не сказал, в каком я режиме игры (PlayerData не пришёл) — " +
                "страховку «живу в выживании» не проверял");

        // Уже в выживании — страховать нечего, и молчание здесь честное:
        // ничего не случилось
        if (mode == Vintagestory.API.Common.EnumGameMode.Survival)
            return new Verdict(null, null);

        // Права заведомо нет: команду НЕ шлём (сервер откажет), но называем
        // вслух и что хотели, и чего лишились
        if (mayGamemode == false)
            return new Verdict(null,
                $"я в режиме {mode} (не выживание), вернуть себя в выживание нечем: " +
                $"права «gamemode» сервер мне не дал, «{command}» слать бесполезно. " +
                "Пока так: летаю и ломаю блоки мгновенно — переключить может только " +
                "тот, у кого это право есть");

        // Право есть (или сервер прав не присылал и проверить нечем) — просим
        // и говорим, о чём просим: ответ сервера будет с чем сверить
        return new Verdict(command,
            $"я в режиме {mode} (не выживание), прошу сервер вернуть меня в выживание: «{command}»" +
            (mayGamemode == null ? " — прав своих не знаю, сервер их не присылал" : ""));
    }

    /// <summary>
    /// ПОЧЕМУ СТЕНД НЕ ПОБЕЖИТ — то же самое право, спрошенное у того же
    /// источника. null значит «беги».
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="Decide"/>. Страховка при входе решает, надо
    /// ли ВЕРНУТЬСЯ в выживание; стенды («!стенд», «!ямтест») решают, можно ли
    /// УЙТИ в творческий. Вопрос разный, право одно и то же — и спрашивается
    /// оно здесь же, а не третьим способом в двух разных командах.
    ///
    /// ЖИВАЯ БЕДА. Оба стенда слали «/gamemode 2» и «/player &lt;имя&gt; gamemode
    /// creative» ВСЛЕПУЮ, ничего не спросив. На сервере заказчика права
    /// «gamemode» нет, и стенд шёл дальше как ни в чём не бывало: бот оставался
    /// в выживании, копал руками, стенд врал результатом, а в журнале лежали два
    /// безымянных отказа сервера. Проверено по игре 1.22.7 (ilspycmd,
    /// VintagestoryLib): и подкоманда «/player … gamemode»
    /// (<c>.RequiresPrivilege(Privilege.gamemode)</c>), и «/gamemode» на СЕБЯ
    /// (<c>handleGameMode</c>: <c>if (flag &amp;&amp; !HasPrivilege(Privilege.gamemode))</c>)
    /// требуют одного и того же права.
    ///
    /// ПРАВ СЕРВЕР НЕ ПРИСЫЛАЛ (null) — СТЕНД НЕ ЗАПРЕЩАЕМ. «Не знаю» — это не
    /// «нельзя»: запрети мы стенд на молчащем сервере, человек получил бы отказ
    /// там, где всё работало.
    /// </summary>
    /// <param name="mayGamemode">Ответ <see cref="SelfState.May"/> про право «gamemode».</param>
    public static string? WhyStandCannotRun(bool? mayGamemode) =>
        mayGamemode == false
            ? "стенд не побежит: он переключает меня в творческий режим, а права " +
              "«gamemode» сервер мне не дал — команда вернулась бы отказом, и я " +
              "копал бы руками, выдавая это за стенд. Нужно право «gamemode» " +
              "(его даёт тот, кто держит сервер)"
            : null;
}

/// <summary>
/// Отладочные команды для проверки подсистем библиотеки: поиск пути, вода,
/// обрывы, бой. Подключаются по флагу --diag и в обычной роли не нужны.
///
/// КАЖДЫЙ ПОХОД ОТСЮДА ИДЁТ <c>a.Token</c>, И ЭТО НЕ УКРАШЕНИЕ. Команда с
/// телом (<c>ChatCommands.Add(needsBody)</c>, заводское «да») берёт владение
/// у распорядителя и кладёт его токен в <see cref="ChatCommandArgs.Token"/> —
/// то есть <c>a.Token</c> ЕСТЬ токен взятого тела, а не «срок команды».
///
/// ЖИВАЯ БЕДА, ради которой это сказано вслух. Девять походов этого файла
/// звали дорогу вовсе без токена: «!дойди» держал тело приказом, а шёл сам по
/// себе. Перебей приказ рефлексом (бой, буря, голод) — владение уходит, а
/// поход идёт дальше чужим телом ещё пять минут, и живой караул
/// (<c>Movement.WalkedOnBorrowedBody</c>) печатает человеку «И ШЁЛ Я ЧУЖИМ
/// ТЕЛОМ» про его же законную команду. Обвинение ложное, а прочтут его один
/// раз — и перестанут читать все.
///
/// Стережёт это <c>НиктоНеХодитБезТелаTests</c>: караул по исходникам обходит
/// все проекты решения, кроме стендов, и этот файл в их числе.
/// </summary>
public static class DiagnosticCommands
{
    /// <summary>Стенды строятся по одному — команды меняют режим и позицию бота.</summary>
    private static readonly SemaphoreSlim standLock = new(1, 1);

    /// <summary>
    /// Проговорить в чат список сообщений через паузу после входа в мир —
    /// удобно гонять сценарии проверок без живого игрока.
    /// Формат: "текст1;текст2;..." (лимит чата соблюдается автоматически).
    /// </summary>
    public static void RunScriptOnJoin(VsBot bot, string script)
    {
        bot.OnJoined += () => _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            foreach (var line in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
                await bot.SayAsync(line.Trim());
        });
    }

    /// <summary>
    /// КТО СТАРШЕ, ЧАТ ИЛИ НАСТРОЙКА РОЛИ: настройка роли. Возвращает готовый
    /// «хвост» ответа человеку — сказать его обязательно.
    ///
    /// ЖИВАЯ БЕДА, ради которой это появилось. Команды «!месть» и «!бой отступ»
    /// писали ПРЯМО в способность самообороны — в те же поля, которые роль
    /// доносит туда заново на КАЖДЫЙ поворот любой своей ручки. Человек говорил
    /// в чат «не мсти», потом двигал в панели ползунок радиуса карьера — и
    /// месть возвращалась. МОЛЧА. Со стороны это выглядит как «бот не слушается».
    ///
    /// Здесь только ДЕЙСТВИЕ; что сказать человеку, решает чистое правило
    /// <see cref="RoleKnobs.SayWhoKeeps"/> — его же и проверяет приёмка.
    /// </summary>
    /// <param name="bot">Бот: у него спрашиваем роль.</param>
    /// <param name="knob">Имя ручки роли («Мстить»).</param>
    /// <param name="value">Значение как есть: число, флаг, значение перечисления.</param>
    /// <param name="temporary">
    /// Что сделать, когда ручки у роли нет: поправить способность напрямую.
    /// Зовётся ТОЛЬКО в этом случае — иначе у одного числа вышло бы два хозяина.
    /// </param>
    private static string ЧерезРоль(VsBot bot, string knob, object? value, Action temporary)
    {
        bool ручкаЕсть = RoleKnobs.Has(bot.Role, knob);
        // Поворот ручки САМ доносит новое значение до способности (Apply зовёт
        // OnKnobsChanged) — второй раз руками нельзя
        string? жалоба = ручкаЕсть ? RoleKnobs.Set(bot.Role!, knob, value) : null;
        if (!ручкаЕсть)
            temporary();
        return RoleKnobs.SayWhoKeeps(knob, bot.Role?.Name, ручкаЕсть, жалоба);
    }

    public static void AddTo(VsBot bot)
    {
        // --- Проверки новых механизмов (28.07) ---

        bot.Commands.Add("часы", "время, сезон, бури", a => a.ReplyAsync(
            $"{bot.Clock}; ночь: {bot.Clock.IsNight}; до темноты {bot.Clock.HoursUntilNight:0.#} ч; " +
            (bot.Storms.Known
                ? bot.Storms.Active ? $"БУРЯ ({bot.Storms.Strength})"
                                    : $"буря ({bot.Storms.Strength}) через {bot.Storms.DaysUntilStorm:0.#} сут"
                : "про бури данных нет")));

        bot.Commands.Add("предмет", "предмет <код> — что игра о нём знает", a =>
        {
            if (a.Words.Length < 1)
                return a.ReplyAsync("нужно: !предмет <код>");
            string code = a.Words[0];
            var col = bot.World.GetCollectible(code);
            if (col == null)
                return a.ReplyAsync($"{code}: в реестре нет");
            return a.ReplyAsync($"{code}: стак {bot.World.MaxStackSize(code)}, " +
                                $"инструмент {bot.World.ToolOf(code)?.ToString() ?? "нет"}, " +
                                $"тир {bot.World.ToolTier(code)}, прочность {bot.World.Durability(code)}, " +
                                $"скорость по камню {bot.World.MiningSpeed(code, Vintagestory.API.Common.EnumBlockMaterial.Stone):0.##}");
        });

        bot.Commands.Add("сколько", "сколько <x> <y> <z> — за сколько сломаю блок", a =>
        {
            if (!a.TryGetCell(out var cell))
                return a.ReplyAsync("нужно: !сколько <x> <y> <z>");
            string code = bot.World.GetBlockCode(cell) ?? "пусто";
            int need = bot.World.RequiredMiningTier(cell.X, cell.Y, cell.Z);
            var tool = bot.Mining.BestToolFor(cell);
            return a.ReplyAsync($"{code}: материал {bot.World.BlockMaterial(cell.X, cell.Y, cell.Z)}, " +
                                $"нужен тир {need}, лучший инструмент {tool?.Content.Code ?? "руки"}, " +
                                $"время {bot.Mining.SecondsToBreak(cell, tool?.Content.Code):0.#} с");
        });

        // --- Новые умения: камень, сбор, крафт, карьер ---

        // Самая важная проверка перед всеми остальными: рецепты и реестры
        // приходят от сервера ОДИН РАЗ при входе. Не разобрались — бот молча
        // «ничего не умеет», и это надо видеть сразу, а не гадать
        bot.Commands.Add("умею", "что бот сейчас умеет: рецепты, тёска, сбор", a =>
        {
            int knap = bot.Knapping.Recipes.Count, craft = bot.Crafting.All.Count;
            return a.ReplyAsync(
                $"Тесать из камня: {(knap > 0 ? $"да, {knap} рецептов" : "НЕТ — рецепты не пришли")}. " +
                $"Крафтить: {(craft > 0 ? $"да, {craft} рецептов" : "НЕТ — рецепты не пришли")}. " +
                $"Подбирать с земли: {(bot.Gathering.RegistryReady ? "да" : "НЕТ — реестр не разобран")}. " +
                $"Копать: {(bot.World.GameItemsReady ? "да" : "НЕТ — реестр предметов не готов")}.");
        });

        // ЛОТОК: СПРОСИТЬ, НЕ ТРОГАЯ МИР. Ради того же, ради чего заведён
        // «!какдобыть»: человек должен видеть, что бот думает про промывку, ДО
        // того как бот уйдёт стоять в воде на семь минут
        bot.Commands.Add("лоток", "лоток [что] — что бот знает про промывку: чем, где и с каким шансом", a =>
        {
            var л = bot.Panning;
            if (!л.Ready)
                return a.ReplyAsync("Реестр блоков сервера ещё не пришёл — про лоток сказать нечего");

            string нужно = a.Text.Trim();
            var части = new List<string>
            {
                $"лотков в реестре {bot.World.PanningDrops.Count} " +
                $"({string.Join(", ", bot.World.PanningDrops.Keys.Take(3))})",
                л.CarriedPan is { } свой ? $"свой лоток: {свой}" : "своего лотка НЕТ",
                л.Allowed ? "мыть разрешено ролью" : "мыть ЗАПРЕЩЕНО ролью («МытьЛоткомКогдаИначеНегде»)",
                л.FeetInWater ? "ноги в воде" : "ноги не в воде"
            };

            if (нужно.Length > 0)
            {
                var пути = л.WaysTo(нужно);
                части.Add(пути.Count == 0
                    ? $"«{нужно}» лотком не намыть: ни один промываемый блок его не отдаёт"
                    : "чем намыть " + нужно + ": " + string.Join("; ", пути.Take(3).Select(п =>
                        $"{п.Material} — {п.Chance:0.###} за промывку, " +
                        $"на 12 штук {Panning.PansFor(п.Chance, 12)} промывок")));
            }

            // Место ищем ПОД НАЗВАННОЕ: без этого «мыть можно» отвечало про
            // песок у воды на вопрос про волокна, которых из песка не бывает
            части.Add(л.FindSpot(нужно.Length > 0 ? нужно : null) is { } место
                ? $"мыть можно: стоять в воде {место.Stand}, горсть из " +
                  $"{bot.World.GetBlockCode(место.Material)} {место.Material}"
                : нужно.Length > 0
                    ? $"места, где намыть «{нужно}», рядом нет"
                    : "места для промывки рядом нет");
            return a.ReplyAsync(string.Join(" | ", части));
        });

        bot.Commands.Add("промой", "промой [сколько] [что] — промыть лотком (по умолчанию столько, сколько велит роль)", async a =>
        {
            int сколько = a.Words.Length > 0 && int.TryParse(a.Words[0], out int n) && n > 0
                ? n
                : bot.Panning.PansPerVisit;
            // «что» — чтобы бот искал землю, отдающую ИМЕННО ЭТО, а не любую
            // промываемую у воды (иначе он моет песок, обещая волокна)
            string? ради = a.Words.FirstOrDefault(w => !int.TryParse(w, out _));
            if (!bot.Panning.Allowed)
            {
                await a.ReplyAsync("Мыть лотком запрещено ролью — поверните ручку " +
                                   "«МытьЛоткомКогдаИначеНегде» в разделе «Снаряжение»");
                return;
            }
            await a.ReplyAsync($"Мою лотком {сколько} раз. «!стоп» — прервать");
            var отчёт = await bot.Panning.PanAsync(сколько, ради, a.Token);
            await a.ReplyAsync(отчёт.Message);
        });

        bot.Commands.Add("тесать", "тесать <код> — вытесать инструмент из камня; без кода — что умею", async a =>
        {
            if (a.Words.Length == 0)
            {
                var ready = bot.Knapping.Available().Take(10).ToList();
                if (ready.Count > 0)
                {
                    await a.ReplyAsync("Могу вытесать: " + string.Join(", ", ready.Select(r => r.OutputCode)));
                    return;
                }
                // Пусто — покажем ПОЧЕМУ: что за материал просят рецепты и что
                // на самом деле лежит в сумке. Без этого «нечем» ничего не
                // объясняет, а причина обычно в несовпадении кода
                var wants = bot.Knapping.Recipes
                    .Where(r => !r.IngredientIsBlock)
                    .Select(r => r.IngredientCode).Distinct().Take(5);
                var have = bot.Self.OwnInventories
                    .Where(kv => !kv.Key.StartsWith("character") && !kv.Key.StartsWith("mouse"))
                    .SelectMany(kv => kv.Value)
                    .Where(s => !s.IsEmpty)
                    .Select(s => s.Code!).Distinct().Take(8);
                await a.ReplyAsync($"Тесать нечем. Рецепты просят: {string.Join(", ", wants)}. " +
                                   $"В сумке: {string.Join(", ", have)}");
                return;
            }
            var report = await bot.Knapping.KnapAsync(a.Words[0]);
            await a.ReplyAsync(report.Message);
        });

        bot.Commands.Add("рецепт", "рецепт <часть кода> — найти рецепт и чего не хватает", a =>
        {
            if (a.Words.Length == 0)
                return a.ReplyAsync("нужно: !рецепт <часть кода>, например knife");
            var found = bot.Crafting.FindRecipes(a.Words[0], 6).ToList();
            if (found.Count == 0)
                return a.ReplyAsync($"рецептов с «{a.Words[0]}» не нашёл");

            // БЕРЁМ ПЕРВЫЙ ОТКРЫТЫЙ, А НЕ ПЕРВЫЙ ПОДРЯД.
            //
            // ЖИВАЯ УЛИКА, ПРИЁМКА 27.08 (out/вода-починка/опыты.csv, 04:16:53).
            // Здесь стояло found[0] — то есть порядок реестра, а в bow.json
            // первым лежит bow-crude. Две соседние строки одного прогона:
            //   !какдобыть bow-simple → «не хватает: flaxfibers …»   ← правда
            //   !рецепт bow           → «Для bow-crude не хватает:
            //                            stick 0/3, rope 0/3»        ← ложь
            // Человек читал «принесите 3 палки и 3 верёвки», приносил — и бот
            // их не брал: bow-crude заперт чертой «bowyer». Хуже того, числа
            // из этой самой строки волна процитировала в шести пресетах и в
            // двух руководствах как «настоящий рецепт лука».
            var open = found.FirstOrDefault(r => !Crafting.LockedByTrait(r));
            var коды = string.Join(", ", found.Select(r => r.OutputCode).Distinct());
            if (open is null)
            {
                var черты = string.Join(", ", found
                    .Select(r => r.RequiresTrait).Where(t => t is { Length: > 0 })
                    .Distinct());
                return a.ReplyAsync($"Нашёл: {коды}. Но собрать не смогу ни один: " +
                                    $"все они требуют черту класса ({черты}), " +
                                    "а своего класса я не знаю и сервер такой сбор не примет");
            }
            var missing = bot.Crafting.Missing(open);
            // Про пропущенные запертые говорим вслух — иначе человек не поймёт,
            // почему бот считает материал не для того рецепта, что он видит
            var пропущено = found.Count(Crafting.LockedByTrait);
            return a.ReplyAsync($"Нашёл: {коды}. " +
                                $"Для {open.OutputCode} не хватает: " +
                                (missing.Count == 0
                                    ? "ничего, могу делать"
                                    : string.Join(", ", missing.Select(m => $"{m.Ingredient} {m.Have}/{m.Need}"))) +
                                (пропущено > 0
                                    ? $" (мимо прошло запертых чертой класса: {пропущено})"
                                    : ""));
        });

        bot.Commands.Add("могу", "что бот может скрафтить прямо сейчас", a =>
            a.ReplyAsync("Могу сделать: " + string.Join(", ", bot.Crafting.WhatCanICraft().Take(15))));

        bot.Commands.Add("сделать", "сделать <код> [сколько] — скрафтить по рецепту", async a =>
        {
            if (a.Words.Length == 0)
            {
                await a.ReplyAsync("нужно: !сделать <код> [сколько]");
                return;
            }
            int qty = a.Words.Length > 1 && int.TryParse(a.Words[1], out int n) ? n : 1;
            await a.ReplyAsync((await bot.Crafting.CraftAsync(a.Words[0], qty)).ToString());
        });

        bot.Commands.Add("лежит", "что лежит на земле вокруг (камни, палки, самородки)", a =>
        {
            var loose = bot.Gathering.FindLoose(20).Take(8)
                .Select(f => $"{f.Code} ({bot.PlayerCoords(f.Pos.X, f.Pos.Y, f.Pos.Z)}, {f.Distance:0.#} бл)")
                .ToList();
            return a.ReplyAsync(loose.Count == 0 ? "рядом ничего не лежит" : string.Join("; ", loose));
        });

        bot.Commands.Add("вещи", "вещи [радиус] — что ВЫПАЛО и лежит сущностью (не блоком)", a =>
        {
            // «Лежит» и «выпало» — разные вещи, и путать их дорого. Камни и
            // палки на земле — это БЛОКИ, их видно через реестр мира. А то, что
            // выпало из сломанной руды, — СУЩНОСТЬ, и её надо переезжать ногами
            if (bot.Self.Position is not { } me)
                return Task.CompletedTask;
            int r = a.Words.Length > 0 && int.TryParse(a.Words[0], out int n) ? n : 20;
            var items = bot.Context.Entities.Nearby(me.X, me.Y, me.Z, r)
                .Where(e => e.IsItem)
                .Select(e => new
                {
                    e,
                    code = bot.Context.DroppedItemCode(e) ?? "?",
                    away = Math.Sqrt((e.X - me.X) * (e.X - me.X) + (e.Y - me.Y) * (e.Y - me.Y) +
                                     (e.Z - me.Z) * (e.Z - me.Z))
                })
                .OrderBy(x => x.away)
                .Take(10)
                .Select(x => $"{x.code} ({bot.PlayerCoords((int)Math.Floor(x.e.X), (int)Math.Floor(x.e.Y), (int)Math.Floor(x.e.Z))}, {x.away:0.#} бл)")
                .ToList();
            return a.ReplyAsync(items.Count == 0
                ? "выпавшего рядом не видно"
                : $"выпало: {string.Join("; ", items)}");
        });

        // ТЕЛО БЕРЁТ САМ ПОДБОР (needsBody: false), А НЕ ОБЁРТКА КОМАНДЫ.
        //
        // Живой случай 19.08, 01:30:10 — команда не работала НИКОГДА и запиралась
        // сама об себя:
        //     [тело] «команда подбери» не берёт тело: занято «команда подбери»
        //     <Tester> не подобрал ничего: тело занято другим делом
        // Разбор тела берёт владение на каждую команду именем «команда подбери»,
        // а Pickup.CollectAsync ниже просит его ВТОРОЙ раз тем же именем и той
        // же важностью — равный равного не перебивает, и подбор честно отказывал
        // сам себе. Владелец обязан быть один; здесь это подбор, потому что
        // только он знает, сколько ему ходить (maxSeconds) и когда всё.
        bot.Commands.Add("подбери", "подбери [радиус] [маска] — сходить за ВЫПАВШИМ вокруг", async a =>
        {
            var w = a.Words;
            double r = w.Length > 0 && double.TryParse(w[0], out double n) ? n : 12;
            string mask = w.Length > 1 ? w[1] : "";
            var got = await bot.Pickup.CollectAsync(r,
                want: mask.Length > 0
                    ? c => c.Contains(mask, StringComparison.OrdinalIgnoreCase)
                    : null,
                maxSeconds: 90, ct: a.Token,
                importance: BodyArbiter.Importance.Command, owner: "команда подбери");
            await a.ReplyAsync(got.ToString());
        }, needsBody: false);

        bot.Commands.Add("собрать", "собрать [радиус] — подобрать всё лежащее вокруг", async a =>
        {
            int r = a.Words.Length > 0 && int.TryParse(a.Words[0], out int n) ? n : 20;
            int taken = await bot.Gathering.GatherAsync(r, 8, 90);
            await a.ReplyAsync(taken > 0 ? $"подобрал: {taken}" : "ничего не подобрал — смотри журнал");
        });

        // СПОСОБ СЪЁМА БЕРЁТСЯ ИЗ НАСТРОЙКИ, А НЕ ИЗ ПОДСКАЗКИ. Обработчик
        // зовёт KeepQuarryingAsync → Plan(), а тот смотрит Quarry.Cut, который
        // роль выставляет из ручки «ровная яма целиком». Прежняя подсказка
        // обещала шахматку безусловно: человек читал «шахматкой», давал
        // «!карьер 6 2» и получал ровную яму. Поэтому в подсказке — откуда
        // берётся способ, а в ответе на саму команду — какой он СЕЙЧАС
        bot.Commands.Add("карьер",
            "карьер <размер> <глубина> — копать карьер; шахматкой или ровной ямой — " +
            "по настройке роли «ровная яма целиком»", async a =>
        {
            // ЦЕНТР СЧИТАЕТ КАРЬЕР, А НЕ КОМАНДА. Здесь стояла своя копия, и она
            // брала КЛЕТКУ НОГ вместо блока под ногами: верхний слой плана
            // приходился на воздух, «!карьер 7 15» обещал 735 клеток и снимал
            // 686, а сорок девять клеток воздуха не попадали ни в сломанные, ни
            // в пропущенные — отчёт выглядел недоделанной работой. Навык роли и
            // инструмент нейросети в тех же координатах копали на слой глубже:
            // одна мерка давала разный результат по тому, каким путём её задали.
            //
            // И отказ теперь ЗВУЧИТ: молчаливый выход человек читает как «бот
            // проглотил команду», а причина у неё одна и называемая
            if (bot.Quarry.TopCentreUnderSelf is not { } here)
            {
                await a.ReplyAsync("не знаю, где стою — карьер начинать не с чего");
                return;
            }
            int size = a.Words.Length > 0 && int.TryParse(a.Words[0], out int s) ? s : 5;
            int depth = a.Words.Length > 1 && int.TryParse(a.Words[1], out int d) ? d : 2;
            await a.ReplyAsync($"Копаю участок {size}×{size}, глубина {depth} — " +
                (bot.Quarry.Cut == QuarryCut.Whole
                    ? "целиком (ровная яма, весь камень крошкой)"
                    : "шахматкой (четверть объёма целыми блоками, до конца работы в яме " +
                      "стоит решётка)") + "…");
            // Токен обязателен: без него карьер продолжает работать вслепую
            // после того, как тело забрал рефлекс, и каждый блок честно
            // выхаживает свои секунды «не дотянуться» — отсюда простои.
            // Через возврат — тем же ходом, что и навык «карьер» у роли:
            // разойдись они, проверка показывала бы не то, что делает бот
            await a.ReplyAsync((await bot.Quarry.KeepQuarryingAsync(here, size, depth,
                bot.Resume, BodyArbiter.Importance.Command, a.Token)).ToString());
        });

        // --- Проверка выживания одной командой (огонь, дикая еда) ---

        bot.Commands.Add("припасы", "выдать себе набор для проверок: дрова, трава, огниво, мотыга", async a =>
        {
            // ЧТО ПРОСИМ — СПИСКОМ, ЧТОБЫ ПОТОМ СВЕРИТЬ С СУМКАМИ.
            //
            // ЗАЧЕМ СВЕРКА ВООБЩЕ ЕСТЬ (закон 4). Здесь стояло безусловное
            // «Выдал набор: дрова, трава, огниво…» сразу после рассылки строк в
            // чат — и рядом комментарий «бот на тестовом сервере в группе
            // admin, команды сервера ему доступны». На мире стенда это
            // НЕПРАВДА: роль бота suplayer, и сервер отвечает «К сожалению, у
            // вас нет прав на использование этой команды» на КАЖДУЮ строку. То
            // есть команда докладывала полный успех, не получив ничего, и
            // прогон, начинавшийся с неё, шёл дальше с пустыми сумками, ища
            // беду не там
            (string Строка, string Код, string Имя)[] набор =
            [
                ("/giveitem firewood 8", "firewood", "дрова"),
                ("/giveitem drygrass 4", "drygrass", "трава"),
                ("/giveitem firestarter 1", "firestarter", "огниво"),
                ("/giveitem hoe-copper 1", "hoe-copper", "мотыга"),
                ("/giveblock soil-medium-none 8", "soil-medium", "земля"),
                ("/giveitem flint 6", "flint", "кремень"),
                ("/giveitem stick 6", "stick", "палки"),
                // Еда обязательна: на тестовом сервере бот успевает умереть от
                // голода и потерять весь набор — так уже сорвалась проверка
                // камнетёсства
                ("/giveitem bread-spelt-perfect 8", "bread-spelt", "хлеб"),
                // Сырое мясо и горшок — для проверки готовки на костре
                ("/giveitem redmeat-raw 4", "redmeat-raw", "мясо"),
                ("/giveblock claypot-fire-fired 1", "claypot", "горшок"),
                // Без сумок у бота ровно 10 слотов — на проверках он упирается
                // в «некуда класть» ещё до дела
                ("/giveitem linensack 2", "linensack", "сумки"),
                // Шестерёнка ставит точку возрождения, нож режет туши
                ("/giveitem gear-temporal 2", "gear-temporal", "шестерёнка"),
                ("/giveitem knife-generic-copper 1", "knife-generic-copper", "нож"),
            ];

            var было = набор.ToDictionary(н => н.Код,
                н => bot.Hands.CountOf(н.Код), StringComparer.OrdinalIgnoreCase);

            foreach (var н in набор)
                await bot.SayAsync(н.Строка);

            // Сумки приходят отдельным пакетом — ждём ФАКТА, а не секунды:
            // как только появилось хоть что-то, дальше ждать незачем
            for (int круг = 0; круг < 20; круг++)
            {
                if (набор.Any(н => bot.Hands.CountOf(н.Код) > было[н.Код]))
                    break;
                await Task.Delay(100, a.Token);
            }

            var пришло = набор.Where(н => bot.Hands.CountOf(н.Код) > было[н.Код])
                              .Select(н => н.Имя).ToList();
            var неПришло = набор.Where(н => bot.Hands.CountOf(н.Код) <= было[н.Код])
                                .Select(н => н.Имя).ToList();

            await a.ReplyAsync(пришло.Count == 0
                ? "Набор НЕ выдан: сервер не дал ничего. /giveitem и /giveblock положены роли " +
                  "admin, а у меня suplayer — права мне поднимать нельзя. Выдайте вещи " +
                  "хозяином мира или другим ботом"
                : $"Выдал набор (по сумкам): {string.Join(", ", пришло)}" +
                  (неПришло.Count > 0 ? $". НЕ пришло: {string.Join(", ", неПришло)}" : ""));
        });

        bot.Commands.Add("шаблоны", "какие шаблоны построек есть в библиотеке", a =>
        {
            var names = bot.Blueprints.Names().ToList();
            return a.ReplyAsync(names.Count == 0
                ? $"Библиотека пуста ({bot.Blueprints.Directory})"
                : $"Шаблоны ({names.Count}): " + string.Join(", ", names.Select(n =>
                {
                    var p = bot.Blueprints.Load(n);
                    return p == null ? n : $"{n} — {p.Blocks.Count} бл {p.Size.X}×{p.Size.Y}×{p.Size.Z}";
                })));
        });

        bot.Commands.Add("сними", "сними <имя> <x1 y1 z1> <x2 y2 z2> — снять область в шаблон", a =>
        {
            var w = a.Words;
            if (w.Length < 7)
                return a.ReplyAsync("Нужно: !сними <имя> <x1 y1 z1> <x2 y2 z2>");
            // «=» перед числом означает абсолютную мировую координату — как
            // в командах игры; без него отсчёт от точки спавна, как на HUD
            var nums = new int[6];
            // Клетку спавна считает VsBot.CoordOffset, а не своё отсечение
            // дроби: «во что игра превращает дробную координату» решается в
            // проекте одним местом (WorldOrigin.Cell)
            var сдвиг = bot.CoordOffset;
            for (int i = 0; i < 6; i++)
            {
                if (!ChatCommandArgs.TryParseCoord(w[i + 1], out double v, out bool abs))
                    return a.ReplyAsync($"Не понял число «{w[i + 1]}»");
                int shift = abs ? 0 : i % 3 == 0 ? сдвиг.X : i % 3 == 2 ? сдвиг.Z : 0;
                nums[i] = (int)Math.Floor(v) + shift;
            }
            var from = new BlockPos(nums[0], nums[1], nums[2]);
            var to = new BlockPos(nums[3], nums[4], nums[5]);

            var print = bot.Blueprints.Capture(w[0], from, to);
            if (print.Blocks.Count == 0)
                return a.ReplyAsync("В этой области нечего снимать");
            bot.Blueprints.Save(print);
            var need = print.Materials().OrderByDescending(kv => kv.Value).Take(6);
            return a.ReplyAsync($"Снял «{print.Name}»: {print.Blocks.Count} блоков " +
                                $"{print.Size.X}×{print.Size.Y}×{print.Size.Z}. Нужно: " +
                                string.Join(", ", need.Select(kv => $"{kv.Key} ×{kv.Value}")));
        });

        bot.Commands.Add("построй", "построй <имя> [x y z] — построить по шаблону (по умолчанию тут)", async a =>
        {
            var w = a.Words;
            if (w.Length == 0)
            {
                await a.ReplyAsync("Нужно: !построй <имя шаблона> [x y z]");
                return;
            }
            if (bot.Blueprints.Load(w[0]) is not { } print)
            {
                await a.ReplyAsync($"Шаблона «{w[0]}» в библиотеке нет — !шаблоны покажет, что есть");
                return;
            }

            BlockPos at;
            if (w.Length >= 4 &&
                ChatCommandArgs.TryParseCoord(w[1], out double bx, out bool absX) &&
                ChatCommandArgs.TryParseCoord(w[2], out double by, out _) &&
                ChatCommandArgs.TryParseCoord(w[3], out double bz, out bool absZ))
            {
                // Клетка сперва, сдвиг потом, и сдвиг — КЛЕТКА спавна:
                // floor(31,9 + 512000,5) промахивался на целый блок
                var сдвиг = bot.CoordOffset;
                var (bwx, bwy, bwz) = WorldOrigin.ToWorld(
                    (int)Math.Floor(bx), (int)Math.Floor(by), (int)Math.Floor(bz),
                    (absX ? 0 : сдвиг.X, absZ ? 0 : сдвиг.Z));
                at = new BlockPos(bwx, bwy, bwz);
            }
            else if (bot.Self.Position is { } me)
                at = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
            else
            {
                await a.ReplyAsync("не знаю, где я");
                return;
            }

            var lack = bot.Blueprints.Missing(print);
            if (lack.Count > 0)
                await a.ReplyAsync("Не хватает: " +
                                   string.Join(", ", lack.Take(6).Select(kv => $"{kv.Key} ×{kv.Value}")) +
                                   " — построю что смогу");
            var report = await bot.Blueprints.BuildAsync(print, at, a.Token);
            await a.ReplyAsync(report.Message);
        });

        bot.Commands.Add("руководство", "руководство <код> — всё, что игра знает об этом предмете", a =>
        {
            string code = a.Text.Trim();
            if (code.Length == 0)
            {
                var (blocks, items, grid, knap) = bot.Handbook.Volume();
                return a.ReplyAsync($"Руководство: блоков {blocks}, предметов {items}, " +
                                    $"рецептов сетки {grid}, тёски {knap}. " +
                                    "Спроси: !руководство <код>");
            }
            var page = bot.Handbook.About(code);
            var parts = new List<string> { page.ToString() };
            if (page.Recipes.Count > 0)
                parts.Add("делается: " + string.Join(" | ", page.Recipes.Take(3)));
            if (page.DroppedBy.Count > 0)
                parts.Add("падает с: " + string.Join(", ", page.DroppedBy.Take(4)));
            if (page.UsedIn.Count > 0)
                parts.Add("идёт в: " + string.Join(", ", page.UsedIn.Take(6)));
            return a.ReplyAsync(string.Join(". ", parts));
        });

        bot.Commands.Add("ищи", "ищи <часть кода> — поиск по руководству", a =>
        {
            string part = a.Text.Trim();
            if (part.Length == 0)
                return a.ReplyAsync("Нужно: !ищи <часть кода>");
            var found = bot.Handbook.Search(part, 15).ToList();
            return a.ReplyAsync(found.Count == 0
                ? $"По «{part}» ничего нет"
                : $"Нашёл ({found.Count}): " + string.Join(", ", found));
        });

        bot.Commands.Add("откуда", "откуда <код> — с чего это падает по реестру сервера", a =>
        {
            string wanted = a.Text.Trim();
            if (wanted.Length == 0)
                return a.ReplyAsync("Нужно: !откуда <код предмета>");
            var sources = bot.Errands.Sources(wanted);
            return a.ReplyAsync(sources.Length == 0
                ? $"В реестре нет блока, с которого падает \"{wanted}\""
                : $"\"{wanted}\" даёт блоков {sources.Length}: {string.Join(", ", sources.Take(8))}");
        });

        bot.Commands.Add("принеси", "принеси <код> [сколько] [x y z сундука] — добыть и принести", async a =>
        {
            var w = a.Words;
            if (w.Length == 0)
            {
                await a.ReplyAsync("Нужно: !принеси <код> [сколько] [x y z сундука]");
                return;
            }
            string wanted = w[0];
            int count = w.Length > 1 && int.TryParse(w[1], out int n) ? n : 1;
            BlockPos? chest = null;
            if (w.Length >= 5 &&
                ChatCommandArgs.TryParseCoord(w[2], out double cxx, out bool cAbsX) &&
                ChatCommandArgs.TryParseCoord(w[3], out double cyy, out _) &&
                ChatCommandArgs.TryParseCoord(w[4], out double czz, out bool cAbsZ))
            {
                // Клетка сперва, сдвиг потом (см. «сними» выше)
                var сдвиг = bot.CoordOffset;
                var (cwx, cwy, cwz) = WorldOrigin.ToWorld(
                    (int)Math.Floor(cxx), (int)Math.Floor(cyy), (int)Math.Floor(czz),
                    (cAbsX ? 0 : сдвиг.X, cAbsZ ? 0 : сдвиг.Z));
                chest = new BlockPos(cwx, cwy, cwz);
            }

            await a.ReplyAsync($"Взялся: {wanted} ×{count}" +
                               (chest is { } c ? $", сложу в сундук {bot.PlayerCoords(c.X, c.Y, c.Z)}" : ""));
            var report = await bot.Errands.FetchAsync(wanted, count, chest, a.Token);
            await a.ReplyAsync(report.Message);
        });

        bot.Commands.Add("нырни", "нырни [глубина] — уйти под воду на N блоков и доложить", async a =>
        {
            int want = a.Words.Length > 0 && int.TryParse(a.Words[0], out int d) ? d : 3;
            if (bot.Self.Position is not { } me)
            {
                await a.ReplyAsync("не знаю, где я");
                return;
            }
            // Сканируем колонки напрямую, как проверенная команда !вода:
            // у поиска блоков вертикальная полоса узкая, и дно водоёма в неё
            // не попадало — бот честно докладывал «глубокой воды нет»
            int cx = (int)Math.Floor(me.X), cy = (int)Math.Floor(me.Y), cz = (int)Math.Floor(me.Z);
            var deep = new List<(BlockPos Pos, int Depth)>();
            for (int dx = -120; dx <= 120; dx++)
            for (int dz = -120; dz <= 120; dz++)
            for (int dy = 12; dy >= -30; dy--)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (!bot.World.IsWaterSurface(x, y, z))
                    continue;
                int depth = 0;
                while (depth < 40 && bot.World.IsWater(x, y - depth, z))
                    depth++;
                if (depth >= want + 1)
                    deep.Add((new BlockPos(x, y, z), depth));
                break;   // в колонке нужна только поверхность
            }
            deep = deep
                .OrderBy(t => Math.Abs(t.Pos.X - cx) + Math.Abs(t.Pos.Z - cz))
                .Take(1)
                .ToList();
            if (deep.Count == 0)
            {
                await a.ReplyAsync($"Воды глубже {want} блоков рядом не нашёл — попробуй !вода");
                return;
            }
            var spot = deep[0];
            var target = new BlockPos(spot.Pos.X, spot.Pos.Y - want, spot.Pos.Z);
            await a.ReplyAsync($"Ныряю на {want} бл: цель {bot.PlayerCoords(target.X, target.Y, target.Z)}, " +
                               $"глубина водоёма {spot.Depth}");
            bool ok = await bot.Movement.MoveToCellAsync(target, ct: a.Token);
            var now = bot.Self.Position;
            double reached = now is { } p ? spot.Pos.Y - p.Y : 0;
            await a.ReplyAsync($"{(ok ? "Дошёл" : "Не дошёл")}: сейчас на глубине {reached:0.#} бл, " +
                               $"воздуха {bot.Entities.Self?.OxygenFraction:P0}");
        });

        bot.Commands.Add("возрождение", "возрождение [тут] — где точка возрождения; «тут» — поставить её здесь", async a =>
        {
            if (a.Words.Length > 0 && a.Words[0].StartsWith("ту"))
            {
                bool ok = await bot.Sleeping.SetRespawnHereAsync(a.Token);
                await a.ReplyAsync(ok ? "Точка возрождения переставлена" : "Не вышло — причина в журнале");
            }
            var point = bot.Sleeping.RespawnPoint;
            await a.ReplyAsync(point is { } p
                ? $"Возрождаюсь на {bot.PlayerCoords(p.X, p.Y, p.Z)}; шестерёнок в сумках: " +
                  $"{bot.Hands.CountOf(bot.Sleeping.RespawnItemCode)}"
                : "Сервер точку возрождения ещё не присылал");
        });

        bot.Commands.Add("кровать", "какая кровать рядом и можно ли сейчас лечь", a =>
        {
            var bed = bot.Sleeping.FindBedNear(radius: 20);
            if (bed == null)
                return a.ReplyAsync($"Кровати рядом не вижу. Усталость {bot.Sleeping.Tiredness:0.#} " +
                                    $"из {bot.Sleeping.MaxTiredness:0.#}");
            bot.Sleeping.CanSleepNow(bed, out string reason);
            return a.ReplyAsync($"Кровать {bed.Code} на {bot.PlayerCoords(bed.Pos.X, bed.Pos.Y, bed.Pos.Z)}, " +
                                $"сон даёт {bed.SleepHours(bot.Clock.HoursPerDay):0.#} ч. " +
                                $"Усталость {bot.Sleeping.Tiredness:0.#}. {reason}");
        });

        bot.Commands.Add("спать", "лечь в ближайшую кровать", async a =>
        {
            bool ok = await bot.Sleeping.GoToBedAsync(ct: a.Token);
            await a.ReplyAsync(ok
                ? $"Лёг на {bot.Sleeping.SleepingOn}"
                : "Лечь не вышло — причина в журнале");
        });

        bot.Commands.Add("убей", "убей <часть кода> — забить ближайшего зверя (для проверки разделки)", async a =>
        {
            string mask = a.Text.Trim();
            if (mask.Length == 0 || bot.Self.Position is not { } me)
            {
                await a.ReplyAsync("Нужно: !убей <часть кода зверя>");
                return;
            }
            // ЖИВОЕ — ТЕМ ЖЕ ПРАВИЛОМ, ЧТО И БОЙ. Здесь стояла ЧЕТВЁРТАЯ
            // рукописная копия догадки о здоровье («e.Health is > 0»): стрелу
            // она отсекала, а соломенное чучело — нет (strawdummy.json игры
            // 1.22.7: тег «inanimate», но health 100/100). Вопрос к реестру
            // сервера задаёт за всех CombatTargets.CanBeFought
            var target = bot.Entities.Nearby(me.X, me.Y, me.Z, 24)
                .Where(e => !e.IsPlayer && !e.IsItem &&
                            e.Code.Contains(mask, StringComparison.OrdinalIgnoreCase) &&
                            CombatTargets.CanBeFought(
                                bot.EntityTypes.Inanimate(e.Code), e.Health))
                .OrderBy(e => e.DistanceTo(me.X, me.Y, me.Z))
                .FirstOrDefault();
            if (target == null)
            {
                await a.ReplyAsync($"Живого \"{mask}\" рядом не вижу");
                return;
            }
            await a.ReplyAsync($"Бью {target.Code} #{target.Id} ({target.Health:0.#} хп)");
            for (int i = 0; i < 25 && !a.Token.IsCancellationRequested; i++)
            {
                // Тем же правилом, что и конец боя: пятой рукописной копии
                // «hp <= 0» тут быть не должно
                if (bot.Entities.Get(target.Id) is not { } live ||
                    CombatTargets.Defeated(live.Health))
                    break;
                // Убежал далеко — не гоняемся до края света: это проверка
                // разделки, а не охоты
                if (bot.Self.Position is { } here && live.DistanceTo(here.X, here.Y, here.Z) > 25)
                    break;
                await bot.Movement.ApproachAsync(() =>
                {
                    var e = bot.Entities.Get(target.Id);
                    return (e?.X ?? live.X, e?.Y ?? live.Y, e?.Z ?? live.Z);
                }, stopDistance: 1.5, maxSeconds: 6, ct: a.Token);
                await bot.Actions.AttackEntityAsync(target.Id);
                await Task.Delay(700, a.Token).ContinueWith(_ => { });
            }
            // «ГОТОВ» — ТОЛЬКО ПО НУЛЮ ОТ СЕРВЕРА, как и в бою и в «!атакуй».
            // Прежде «after?.Health is > 0» сваливало в «готов» и случай
            // after == null: цель просто вышла из прогруженных чанков — а
            // человека посылали её разделывать. Это та же заявка на победу
            // без факта, из-за которой журнал 17.08 насчитал сто восемьдесят
            // побед над одной стрелой
            var after = bot.Entities.Get(target.Id);
            await a.ReplyAsync(after == null
                ? $"{target.Code} пропал из виду — победы не считаю"
                : CombatTargets.Defeated(after.Health)
                    ? $"{target.Code} готов — теперь !разделай"
                    : $"Не добил: осталось {after.Health:0.#} хп");
        });

        bot.Commands.Add("читы", "читы [всё|нет|<имя>] — что нечестного включено", a =>
        {
            var w = a.Words;
            if (w.Length > 0)
            {
                switch (w[0])
                {
                    case "всё" or "все": bot.Cheats.All(); break;
                    case "нет" or "выкл": bot.Cheats.Enabled = false; break;
                    default:
                        bot.Cheats.Enabled = true;
                        switch (w[0])
                        {
                            case "рука": bot.Cheats.InfiniteReach = true; break;
                            case "сквозь": bot.Cheats.NoLineOfSight = true; break;
                            case "мгновенно": bot.Cheats.InstantMining = true; break;
                            case "тир": bot.Cheats.IgnoreToolTier = true; break;
                            case "телепорт": bot.Cheats.Teleport = true; break;
                            case "падение": bot.Cheats.NoFallDamage = true; break;
                            case "стены": bot.Cheats.SeeThroughWalls = true; break;
                            case "сундуки": bot.Cheats.SeeInsideContainers = true; break;
                            default: return a.ReplyAsync(
                                "Знаю: рука, сквозь, мгновенно, тир, телепорт, падение, " +
                                "стены, сундуки, всё, нет");
                        }
                        break;
                }
            }
            return a.ReplyAsync(bot.Cheats.ToString());
        });

        bot.Commands.Add("месть", "месть [не|рядом|догнать] [радиус] — мстить ли тому, кто ударил издалека", a =>
        {
            var combat = bot.Behaviors.Get<BehaviorAttackHostiles>();
            if (combat == null)
                return a.ReplyAsync("Способность самообороны не подключена");
            var w = a.Words;
            // Обе ручки — «Мстить» и «РадиусМести» — живут у роли (LivingRole), и
            // чат меняет ИМЕННО ИХ. Прямая запись в способность осталась
            // запасным ходом для роли, у которой этих настроек нет
            string сказать = "";
            if (w.Length > 0)
            {
                var кому = w[0] switch
                {
                    "не" or "нет" => RevengePolicy.Никогда,
                    "рядом" => RevengePolicy.Рядом,
                    _ => RevengePolicy.Догонять
                };
                сказать += ЧерезРоль(bot, "Мстить", кому, () => combat.Revenge = кому);
            }
            if (w.Length > 1 && double.TryParse(w[1], out double r))
                сказать += ЧерезРоль(bot, "РадиусМести", r, () => combat.RevengeRadius = r);
            // Числа читаем У СПОСОБНОСТИ, а не у ручки: роль вправе прижать
            // вписанное к краю («радиус не меньше нуля»), и человек должен
            // увидеть то, с чем бот на самом деле пойдёт в бой
            return a.ReplyAsync($"Месть: {combat.Revenge}, радиус {combat.RevengeRadius:0} бл, " +
                                $"погоня до {combat.RevengeChaseSeconds:0} с, " +
                                $"память об ударе {combat.RevengeMemorySeconds:0} с." + сказать);
        });

        bot.Commands.Add("туши", "какие туши видно вокруг и что с ними можно сделать", a =>
        {
            var carcasses = bot.Butchering.CarcassesNear(24).ToList();
            var lootable = bot.Butchering.LootableNear(24).ToList();

            // ТРЕТИЙ СПИСОК ОБХОДА — ТОТ ЖЕ САМЫЙ, И СПРАШИВАЕТСЯ ОН У ОБХОДА.
            //
            // Вскрытая пустая туша, окно у которой не закрывали, — это работа,
            // за которой бот ПОЙДЁТ (Butchering.UnrestedNear): исчезновение
            // туши в игре висит на закрытии окна. Пока окно про неё молчало,
            // человек за одну секунду читал «Туш вокруг не вижу» — и тут же
            // «прибрал туш: 1» от «!разделай». Ровно такую пару взаимоисключающих
            // строк проект уже ловил 19.08 в 18:33:51, и правило с тех пор одно:
            // одна мерка на всех, кто про туши говорит
            var неприбранные = bot.Butchering.UnrestedNear(24).ToList();

            // ТЕЛА, КОТОРЫЕ РАЗДЕЛЫВАТЬ НЕЧЕМ И НЕЗАЧЕМ, тоже надо назвать:
            // иначе человек, только что убивший саранчу, читает «туш не вижу»
            // и решает, что бот ослеп. С неё всё выпало на землю — это подбор
            var спавшие = bot.Self.Position is { } где
                ? bot.Entities.Nearby(где.X, где.Y, где.Z, 24)
                    .Where(e => bot.Butchering.IsCarcass(e) && !Butchering.CanBeButchered(e))
                    .ToList()
                : [];
            if (carcasses.Count == 0 && lootable.Count == 0 && неприбранные.Count == 0)
                return a.ReplyAsync(спавшие.Count == 0
                    ? "Туш вокруг не вижу"
                    : "Разделывать нечего, но тела рядом есть: " +
                      string.Join(", ", спавшие.Select(e => e.Code)) +
                      " — с них всё выпало на землю, это подбор");
            string Describe(IEnumerable<EntityInfo> list, string what) =>
                string.Join(", ", list.Select(e =>
                {
                    var inside = bot.Butchering.Contents(e.Id);
                    // Опись пустой туши — не «[]», а молчание: скобки с пустотой
                    // человек читает как поломку, а пустота тут и есть суть
                    string loot = inside == null || !inside.Any(s => !s.IsEmpty) ? "" :
                        " [" + string.Join(", ", inside.Where(s => !s.IsEmpty)
                            .Select(s => $"{s.Code} x{s.Count}")) + "]";
                    return $"{e.Code} #{e.Id} ({bot.PlayerCoords(e.X, e.Y, e.Z)}) — {what}{loot}";
                }));
            return a.ReplyAsync(string.Join("; ",
                new[]
                {
                    Describe(carcasses, "разделать"),
                    Describe(lootable, "забрать добычу"),
                    Describe(неприбранные, "закрыть окно — вскрыта и пуста, а с земли не ушла")
                }.Where(s => s.Length > 0)));
        });

        bot.Commands.Add("разделай", "разделай [радиус] — разделать туши вокруг и забрать добычу", async a =>
        {
            double radius = a.Words.Length > 0 && double.TryParse(a.Words[0], out double r) ? r : 16;
            var итог = await bot.Butchering.ButcherNearbyAsync(radius, a.Token);
            // Итог говорит сам за себя: «ножа нет» и «туш не нашёл» — разные
            // ответы, и человек, отдавший команду, должен видеть, какой из них
            await a.ReplyAsync(итог.ToString());
        });

        bot.Commands.Add("скажи", "скажи <текст> — сказать в чат от своего имени (в т.ч. команду сервера)", async a =>
        {
            // Только для проверок: так со стенда задают время, погоду и убирают
            // мобов. В обычной работе бот команд сервера не отдаёт
            string text = a.Text.Trim();
            if (text.Length == 0)
            {
                await a.ReplyAsync("Что сказать-то?");
                return;
            }
            await bot.SayAsync(text);
        });

        bot.Commands.Add("костёр", "сложить и поджечь костёр рядом с собой (по шагам)", async a =>
        {
            await a.ReplyAsync($"Дрова: {bot.Hands.CountOf("firewood")}, трава: {bot.Hands.CountOf("drygrass")}, " +
                               $"огниво: {bot.Hands.CountOf("firestarter")}");
            var pit = await bot.Fire.MakeCampfireAsync(a.Token);
            if (pit is not { } p)
            {
                await a.ReplyAsync("Костёр не вышел — смотри журнал, там причина по шагам");
                return;
            }
            await a.ReplyAsync($"Костёр ({bot.PlayerCoords(p.X, p.Y, p.Z)}): {bot.World.GetBlockCode(p)}, " +
                               $"горит: {bot.Fire.IsBurning(p)}");
        });

        bot.Commands.Add("пожарь", "пожарь <код> — приготовить на костре (по умолчанию сырое мясо)", async a =>
        {
            string raw = a.Words.Length > 0 ? a.Words[0] : "redmeat-raw";
            var pit = bot.Fire.NearestFirepit(6);
            await a.ReplyAsync(pit is not { } p
                ? $"Костра рядом нет — сложу свой и пожарю {raw}"
                : $"Жарю {raw} на костре {bot.PlayerCoords(p.X, p.Y, p.Z)}");
            var got = await bot.Cooking.CookAsync(raw, pit, a.Token);
            // Причину говорит сама кухня, и говорит В ОТВЕТ, а не «в журнале»:
            // человек спросил в чате и в чате же обязан прочесть, чего не вышло
            await a.ReplyAsync(got.Cooked == null
                ? $"Не приготовилось: {got.Why}"
                : $"Готово: {got.Cooked}");
        });

        bot.Commands.Add("вкостре", "что лежит в ближайшем костре по слотам", a =>
        {
            if (bot.Fire.NearestFirepit(8) is not { } pit)
                return a.ReplyAsync("Костра рядом не вижу");
            string where = bot.PlayerCoords(pit.X, pit.Y, pit.Z);
            var slots = bot.Cooking.Contents(pit);
            if (slots == null)
                return a.ReplyAsync($"Костёр {where} содержимого не показал");
            string[] names = ["топливо", "вход", "выход", "горшок 1", "горшок 2", "горшок 3", "горшок 4"];
            var lines = slots.Select((s, i) =>
                $"{(i < names.Length ? names[i] : i.ToString())}: {(s.Code == null ? "—" : $"{s.Code} ×{s.Count}")}");
            return a.ReplyAsync($"Костёр {where} (горит: {bot.Fire.IsBurning(pit)}) — {string.Join(", ", lines)}");
        });

        bot.Commands.Add("возьми", "возьми [слот] — забрать из костра по шагам, с отчётом об инвентарях", async a =>
        {
            if (bot.Fire.NearestFirepit(8) is not { } pit)
            {
                await a.ReplyAsync("Костра рядом не вижу");
                return;
            }
            int slot = a.Words.Length > 0 && int.TryParse(a.Words[0], out int s) ? s : Cooking.OutputSlot;
            string invId = Cooking.FirepitInventoryId(pit);
            await a.ReplyAsync($"Инвентарь костра: {invId}; в слоте {slot}: " +
                               $"{bot.Cooking.SlotAt(pit, slot)?.Code ?? "пусто"}");
            await a.ReplyAsync("Знаю инвентарей до открытия: " +
                               string.Join(", ", bot.Self.Inventories.Keys.Where(k => !k.Contains("player", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("hotbar") && !k.StartsWith("backpack") && !k.StartsWith("character") && !k.StartsWith("craftinggrid") && !k.StartsWith("ground") && !k.StartsWith("mouse"))));
            bool ok = await bot.Cooking.TakeOutAsync(pit, slot, a.Token);
            await a.ReplyAsync("Знаю инвентарей после: " +
                               string.Join(", ", bot.Self.Inventories.Keys.Where(k => !k.Contains("player", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("hotbar") && !k.StartsWith("backpack") && !k.StartsWith("character") && !k.StartsWith("craftinggrid") && !k.StartsWith("ground") && !k.StartsWith("mouse"))));
            await a.ReplyAsync(ok ? "Забрал" : $"Не забрал, в слоте по-прежнему {bot.Cooking.SlotAt(pit, slot)?.Code ?? "пусто"}");
        });

        bot.Commands.Add("еда", "что съедобного видно вокруг (кусты, грибы) и спелое ли", a =>
        {
            if (bot.Self.Position is not { } me)
                return a.ReplyAsync("не знаю, где я");
            var here = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
            var found = bot.World.FindBlocks(
                    c => bot.Foraging.IsBush(c) || bot.Foraging.IsBreakableFood(c), here, 24, height: 6, limit: 10)
                .Select(p =>
                {
                    string code = bot.World.GetBlockCode(p) ?? "?";
                    string state = bot.Foraging.IsBush(code)
                        ? bot.Foraging.IsRipeBush(p) ? "СПЕЛЫЙ" : "не спелый"
                        : "ломается";
                    return $"{code} ({bot.PlayerCoords(p.X, p.Y, p.Z)}) — {state}";
                })
                .ToList();
            return a.ReplyAsync(found.Count == 0
                ? "Съедобного в 24 блоках не вижу"
                : string.Join("; ", found));
        });

        bot.Commands.Add("собери", "сходить за дикой едой (радиус 24, до 3 мест) и вернуться", async a =>
        {
            // Причину отдаём как есть: «в 24 бл вокруг не видно» и «шесть
            // кустов, но все неспелые» — разные новости, и раньше обе
            // выглядели как «ничего не собрал»
            var итог = await bot.Foraging.GatherAsync(24, maxPlaces: 3, maxSeconds: 60);
            await a.ReplyAsync(итог.Reason);
        });

        bot.Commands.Add("вспаши", "вспаши <x> <y> <z> — проверка удержания кнопки (мотыга)", async a =>
        {
            if (!a.TryGetCell(out var cell))
            {
                await a.ReplyAsync("нужно: !вспаши <x> <y> <z>");
                return;
            }
            string was = bot.World.GetBlockCode(cell) ?? "пусто";
            bool ok = await bot.Farming.TillAsync(cell);
            await a.ReplyAsync($"{was} → {bot.World.GetBlockCode(cell) ?? "пусто"} ({(ok ? "вспахано" : "нет")})");
        });

        bot.Commands.Add("копай", "копай <x> <y> <z> — честно сломать блок", async a =>
        {
            if (!a.TryGetCell(out var cell))
            {
                await a.ReplyAsync("нужно: !копай <x> <y> <z>");
                return;
            }
            var result = await bot.Mining.BreakAsync(cell);
            await a.ReplyAsync($"{cell}: {result}");
            if (result.Success)
                await bot.Mining.CollectDropsAsync();
        });


        // Правила магазина живут в РОЛИ, а не в библиотеке: библиотека даёт
        // умение читать таблички и сундуки, а что значат !STORE, !cash и
        // цены — решает конкретный торговец
        var shop = new TraderShop(bot.Context, bot.Read);
        shop.OnLog += m => bot.Log($"[торговля] {m}");
        // Покупателю — в общий чат, и это ОТВЕТ ему (он обратился действием),
        // а не рассказ бота о своих делах: радиотишина такое не глушит
        shop.OnAnnounce += text => bot.SayAsync(text, reason: SayReason.Ответ);

        // Страховка: стенды переключают бота в креатив, и если прошлый прогон
        // упал на середине, бот остался бы летать. Он живёт в выживании.
        //
        // ЧТО ЗДЕСЬ БЫЛО НЕ ТАК. Команда уходила ВСЛЕПУЮ, каждый прогон, и на
        // сервере заказчика сервер отвечал двумя строками без имени команды:
        //   [чат:0] К сожалению, у вас нет прав на использование этой команды
        //   [чат:0] For help, type /help player
        // Человек по журналу не мог узнать ни ЧТО спрошено, ни чего бот лишился
        // (правило 4 проекта). А спрашивать было и не о чем: свой режим игры
        // сервер называет сам, пакетом PlayerData(41), за три секунды до этого.
        bot.OnJoined += () => _ = Task.Run(async () =>
        {
            // Три секунды — чтобы сервер успел прислать PlayerData(41) с
            // режимом и правами; без них решать не по чему
            await Task.Delay(3000);
            var verdict = ModeInsurance.Decide(
                bot.Self.GameMode,
                bot.Self.May(Vintagestory.API.Server.Privilege.gamemode),
                bot.Name);
            if (verdict.Say is { } say)
                bot.Log($"[режим] {say}");
            if (verdict.Command is { } command)
                await bot.SayAsync(command);
        });

        bot.Commands.Add("блок", "блок x y z — что за блок в точке (всё, что о нём знает модель)", async a =>
        {
            if (!a.TryGetCellPrefix(out var p))
            {
                await a.ReplyAsync("Нужно: !блок <x> <y> <z>");
                return;
            }
            var w = bot.World;
            string code = w.GetBlockCode(p.X, p.Y, p.Z) ?? "<реестр не получен>";
            string liquid = w.GetLiquidCode(p.X, p.Y, p.Z) ?? "нет";

            // Блок-сущность целиком: именно там живёт состояние дверей и люков
            string be = "нет";
            if (w.GetBlockEntity(p) is { } info)
            {
                string attrs = info.Attributes is { } t
                    ? string.Join(" ", t.Select(kv => $"{kv.Key}={kv.Value.GetValue()}"))
                    : "";
                be = $"{info.ClassName}[{attrs}]";
            }

            // Коллизия ТОГО ЖЕ объекта блока, которым оперирует физика игры, —
            // спрашиваем ОДНИМ входом (WorldModel.PhysicsBlock). Здесь стоял
            // сырой GetGameBlock, и у РАСПАХНУТОЙ двери команда печатала
            // коробку ЗАКРЫТОЙ створки [0..1 0..1 0,88..1]: живой прогон 19.08
            // потерял на этом полчаса, разбирая застревание в проёме по
            // показаниям, которые врали
            var gb = bot.World.PhysicsBlock(p.X, p.Y, p.Z);
            string boxes = gb?.CollisionBoxes is { Length: > 0 } bx
                ? string.Join(" ", bx.Select(b =>
                    $"[{b.X1:0.##}..{b.X2:0.##} {b.Y1:0.##}..{b.Y2:0.##} {b.Z1:0.##}..{b.Z2:0.##}]"))
                : "НЕТ (тело проходит насквозь)";

            // ЖЖЁТ ЛИ ЭТА КЛЕТКА — печатаем ЖИВОЙ ответ модели, а не пересказ.
            // Без этой строки живой прогон не мог отличить «правило верно» от
            // «правило верно, но реестр сервера нужных полей не принёс»: класс
            // блока и атрибут insideDamage приходят пакетом, и проверить их
            // можно только на живом сервере
            string вред = w.HarmAt(p.X, p.Y, p.Z) switch
            {
                CellHarm.Always => "ЖЖЁТ СТОЯЩЕГО",
                CellHarm.IfRunning => "бьёт вбежавшего",
                _ => "нет"
            };

            // ЧЕМ ИМЕННО ИГРА ОБЪЯВИЛА ВИНОВНИКА — три поля реестра, на которых
            // стоит правило вреда (HarmRules.HarmOf). Печатаются они затем, что
            // на живом сервере это ЕДИНСТВЕННЫЙ способ отличить «правило верно»
            // от «правило верно, а полей сервер не прислал».
            //
            // ВИНОВНИК, А НЕ БЛОК ИЗ СЛОЯ БЛОКОВ. Здесь стоял gb (слой блоков),
            // и над лужей лавы команда честно печатала «вред=ЖЖЁТ СТОЯЩЕГО»
            // рядом с «класс —, материал Air, insideDamage 0»: лава и кипяток
            // живут в слое ЖИДКОСТЕЙ, а в слое блоков над ними воздух. Живой
            // стол прошлой волны снят с костра, где эти два слоя совпадают, и
            // расхождения было не видно
            string виновник = w.HarmCodeAt(p.X, p.Y, p.Z) ?? "—";
            var вб = виновник == "—" ? gb : w.GetGameBlockByCode(виновник);
            string реестр = вб == null
                ? "объекта блока нет (реестр не собран)"
                : $"класс {вб.Class ?? "?"}, материал {вб.BlockMaterial}, " +
                  $"insideDamage {вб.Attributes?["insideDamage"].AsFloat(0f) ?? 0f:0.##}";

            // ВРЕД — ОТДЕЛЬНОЙ СТРОКОЙ И ПЕРВЫМ, а не хвостом общей.
            //
            // ЖИВОЙ СЛУЧАЙ 27.08. Ответ команды длинный (551 буква), а чат
            // укорачивает его до 195 — и весь хвост, где как раз и стоят
            // «вред», виновник и три поля реестра, на живом сервере ПРОЧЕСТЬ
            // БЫЛО НЕЛЬЗЯ: «…вишу=False…(целиком — в журнале и окне
            // управления)», а в журнале лежит та же укороченная строка. То
            // есть единственный способ отличить живьём «правило верно» от
            // «полей сервер не прислал» был отрезан ножницами чата
            await a.ReplyAsync($"({a.Here(p)}) {code}: стоять={w.IsStandable(p.X, p.Y, p.Z)}, " +
                               $"вред={вред}" +
                               (виновник == "—" ? "" : $", жжёт «{виновник}»") +
                               $" [{реестр}]");

            await a.ReplyAsync($"({a.Here(p)}) = {code}; физбоксы {boxes}; жидкость {liquid}; верх коллизии " +
                               $"{w.GetCollisionTop(p.X, p.Y, p.Z):0.###}; проходим={w.IsPassable(p.X, p.Y, p.Z)}, " +
                               $"лестница={w.IsClimbable(p.X, p.Y, p.Z)}, " +
                               $"вишу={w.CanHangAt(p.X, p.Y, p.Z)}; дверь={w.IsDoor(p.X, p.Y, p.Z)}, " +
                               $"открыта={w.IsDoorOpen(p.X, p.Y, p.Z)}; сущность {be}");
        });

        bot.Commands.Add("столбись", "столбись [сколько] [код блока] — подняться столбом вверх", async a =>
        {
            var w = a.Words;
            int levels = w.Length > 0 && int.TryParse(w[0], out int n) ? n : 3;
            string code = w.Length > 1 ? w[1] : "";
            await a.ReplyAsync($"Столблюсь на {levels} бл" + (code.Length > 0 ? $" из {code}" : ""));
            int built = await bot.Mining.PillarUpAsync(levels, code, a.Token);
            await a.ReplyAsync(built > 0
                ? $"Поднялся на {built} бл, теперь на ({bot.PlayerCoordsHere()})"
                : "Подняться не вышло — причина в журнале");
        });

        bot.Commands.Add("прыг", "прыг — подпрыгнуть и замерить, на сколько поднялся", async a =>
        {
            if (bot.Body?.Physics is not { } phys)
                return;
            double start = phys.Y, peak = phys.Y;
            bool wasGround = phys.OnGround;
            phys.Controls.Jump = true;
            var till = DateTime.UtcNow.AddSeconds(1.2);
            while (DateTime.UtcNow < till)
            {
                await Task.Delay(20, a.Token).ContinueWith(_ => { });
                if (phys.Y > peak) peak = phys.Y;
            }
            phys.Controls.Jump = false;
            await a.ReplyAsync($"Прыжок: с {start:0.##} до {peak:0.##} (+{peak - start:0.##}), " +
                               $"на земле был={wasGround}, сейчас={phys.OnGround}, в воде={phys.Swimming}");
        });

        bot.Commands.Add("заберись", "заберись <x> <y> <z> [блок] — дойти и взять высоту (столбом, если надо)", async a =>
        {
            var w = a.Words;
            if (w.Length < 3 ||
                !ChatCommandArgs.TryParseCoord(w[0], out double tx, out bool absX) ||
                !ChatCommandArgs.TryParseCoord(w[1], out double ty, out _) ||
                !ChatCommandArgs.TryParseCoord(w[2], out double tz, out bool absZ))
            {
                await a.ReplyAsync("Нужно: !заберись <x> <y> <z> [чем столбиться]");
                return;
            }
            // Клетка сперва, сдвиг потом (см. «сними» выше)
            var сдвигЗаберись = bot.CoordOffset;
            var (zwx, zwy, zwz) = WorldOrigin.ToWorld(
                (int)Math.Floor(tx), (int)Math.Floor(ty), (int)Math.Floor(tz),
                (absX ? 0 : сдвигЗаберись.X, absZ ? 0 : сдвигЗаберись.Z));
            var target = new BlockPos(zwx, zwy, zwz);

            await a.ReplyAsync($"Забираюсь на ({bot.PlayerCoords(target.X, target.Y, target.Z)})");
            var got = await bot.Climbing.ClimbToAsync(target, 240, a.Token,
                w.Length > 3 ? w[3] : "");
            await a.ReplyAsync($"{got} ({bot.PlayerCoordsHere()})");
        });

        bot.Commands.Add("прыгвперёд", "прыгвперёд <градусы> [нет] — прыжок вперёд (с бегом или с места), замер полёта", async a =>
        {
            if (bot.Body?.Physics is not { } phys)
                return;
            double yaw = a.Words.Length > 0 && double.TryParse(a.Words[0], out double d)
                ? d * Math.PI / 180 : phys.Yaw;
            bool sprint = a.Words.Length < 2 || a.Words[1] != "нет";
            // Клавиши надо ДЕРЖАТЬ каждый тик: тело сбрасывает их само,
            // и одиночная установка Forward=true ничего не даёт — на этом
            // и врал прошлый замер
            double goalX = phys.X + Math.Sin(yaw) * 30, goalZ = phys.Z + Math.Cos(yaw) * 30;
            double y0 = phys.Y;
            bool climbing = phys.Climbing;

            // На лестнице разгона НЕ БЫВАЕТ: шаг в сторону просто стряхивает
            // тело вниз. Прыгать надо с места, толчком от плиты — и мерить
            // с той же секунды. На этом врал прошлый замер: бот падал
            // восемь блоков ещё до нажатия прыжка
            if (sprint && phys.Climbing)
                sprint = false;
            if (sprint)
            {
                var runUp = DateTime.UtcNow.AddSeconds(1.0);
                while (DateTime.UtcNow < runUp)
                {
                    phys.WalkToward(goalX, goalZ, sprint: true);
                    await Task.Delay(20, a.Token).ContinueWith(_ => { });
                }
            }

            double sx = phys.X, sz = phys.Z;
            double far = 0, peak = y0;
            bool left = false;
            var trail = new List<string>();
            var t0 = DateTime.UtcNow;
            var till = DateTime.UtcNow.AddSeconds(2.5);
            int mark = 0;
            while (DateTime.UtcNow < till)
            {
                phys.WalkToward(goalX, goalZ, sprint);
                phys.Controls.Jump = true;
                await Task.Delay(20, a.Token).ContinueWith(_ => { });
                double dx = phys.X - sx, dz = phys.Z - sz;
                far = Math.Max(far, Math.Sqrt(dx * dx + dz * dz));
                peak = Math.Max(peak, phys.Y);
                // След по шагам: без него не отличить «не оттолкнулся» от
                // «оттолкнулся, но лестница втянула обратно»
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (ms / 200 > mark)
                {
                    mark = ms / 200;
                    // Пишем И нашу высоту, И серверную: если они разъезжаются,
                    // «прыжок не вышел» на самом деле означает «сервер нас
                    // там и не видел»
                    double serverY = bot.Entities.Self?.Y ?? phys.Y;
                    trail.Add($"{ms}мс: {Math.Sqrt(dx * dx + dz * dz):0.##}/" +
                              $"наш{phys.Y - y0:+0.##;-0.##;0} серв{serverY - y0:+0.##;-0.##;0}" +
                              (phys.Climbing ? " лез" : "") + (phys.OnGround ? " земля" : ""));
                }
                if (!phys.OnGround && !phys.Climbing)
                    left = true;
                else if (left)
                    break;
            }
            bot.Log("[прыжок] " + string.Join(" | ", trail));
            phys.Controls.Jump = false;
            phys.Stop();
            await a.ReplyAsync($"Прыжок {(sprint ? "с разбегом" : "с места")}: улетел {far:0.##} бл, " +
                               $"вверх +{peak - y0:0.##}; на лестнице был={climbing}, " +
                               $"сейчас Y {phys.Y:0.##} (было {y0:0.##})");
        });

        bot.Commands.Add("способности", "живы ли способности: пульс цикла, кто действовал последним", a =>
        {
            var r = bot.Behaviors;
            string pulse = r.LastTick is { } t
                ? $"тикал {(DateTime.UtcNow - t).TotalSeconds:0.#} с назад, всего {r.Ticks} тиков"
                : "НЕ ТИКАЛ НИ РАЗУ — цикл не запустился";
            string acted = r.LastActed is { } a2
                ? $"{a2.Who} ({(DateTime.UtcNow - a2.When).TotalSeconds:0} с назад)"
                : "никто ещё не брал ход";
            return a.ReplyAsync($"Способностей {r.Count}: {string.Join(", ", r.Behaviors.Select(b => b.GetType().Name.Replace("Behavior", "")))}. " +
                                $"Цикл: {pulse}. Последним действовал: {acted}");
        });

        bot.Commands.Add("брось", "брось <часть кода> [сколько] — выбросить вещь на землю", async a =>
        {
            var w = a.Words;
            if (w.Length == 0)
            {
                await a.ReplyAsync("Нужно: !брось <часть кода> [сколько]");
                return;
            }
            int count = w.Length > 1 && int.TryParse(w[1], out int n) ? n : 0;
            int dropped = await bot.Hands.DropAsync(
                c => c.Contains(w[0], StringComparison.OrdinalIgnoreCase), count, a.Token);
            await a.ReplyAsync(dropped > 0 ? $"Бросил {dropped} шт." : "Нечего бросать или сервер не дал");
        });

        // ПОДСКАЗКА НАЗЫВАЕТ ВСЕ ПРИЁМЫ, А НЕ ОДИН ИЗ ЧЕТЫРЁХ. Здесь стояло
        // «спуститься (разобрав под собой, если надо)» — слова верные ровно до
        // 01.09, пока разбор под собой и был всем спуском. С тех пор их четыре
        // (Walker.ChooseWayDown: спрыгнуть, ступени, лестница по стене, разбор
        // под собой), и человек, читавший подсказку, не знал, что боту стоит
        // дать лестниц
        bot.Commands.Add("слезь",
            "слезь <x> <y> <z> — спуститься: дорогой, ступенями, лестницей по стене " +
            "или разобрав под собой", async a =>
        {
            var w = a.Words;
            if (w.Length < 3 ||
                !ChatCommandArgs.TryParseCoord(w[0], out double tx, out bool absX) ||
                !ChatCommandArgs.TryParseCoord(w[1], out double ty, out _) ||
                !ChatCommandArgs.TryParseCoord(w[2], out double tz, out bool absZ))
            {
                await a.ReplyAsync("Нужно: !слезь <x> <y> <z>");
                return;
            }
            // Клетка сперва, сдвиг потом (см. «сними» выше)
            var сдвигСлезь = bot.CoordOffset;
            var (swx, swy, swz) = WorldOrigin.ToWorld(
                (int)Math.Floor(tx), (int)Math.Floor(ty), (int)Math.Floor(tz),
                (absX ? 0 : сдвигСлезь.X, absZ ? 0 : сдвигСлезь.Z));
            var target = new BlockPos(swx, swy, swz);
            var got = await bot.Climbing.DescendToAsync(target, 180, a.Token);
            await a.ReplyAsync($"{got} ({bot.PlayerCoordsHere()})");
        });

        bot.Commands.Add("лесенка", "лесенка <высота> — построить лестницу по стене и подняться", async a =>
        {
            int h = a.Words.Length > 0 && int.TryParse(a.Words[0], out int n) ? n : 4;
            await a.ReplyAsync($"Строю лестницу на {h} бл");
            var got = await bot.Scaffolding.LadderUpAsync(h, a.Token);
            await a.ReplyAsync($"{got} ({bot.PlayerCoordsHere()})");
        });

        // …И У ПОДЪЁМА ТО ЖЕ САМОЕ: «лестница или столб» теряло СТУПЕНИ —
        // единственный даровой приём из трёх и первый по цене
        // (Scaffolding.GetOutAsync). Человек читал подсказку и нёс лестницы
        // туда, где хватило бы кирки
        bot.Commands.Add("выберись",
            "выберись <высота> — наверх любым способом: ступенями, лестницей по стене " +
            "или столбом", async a =>
        {
            int h = a.Words.Length > 0 && int.TryParse(a.Words[0], out int n) ? n : 4;
            var got = await bot.Scaffolding.GetOutAsync(h, a.Token);
            await a.ReplyAsync($"{got} ({bot.PlayerCoordsHere()})");
        });

        bot.Commands.Add("руда", "руда [радиус] [металл] — что за руду видно вокруг", a =>
        {
            var w = a.Words;
            int r = w.Length > 0 && int.TryParse(w[0], out int n) ? n : 24;
            string metal = w.Length > 1 ? w[1] : "";
            var found = bot.Ores.Find(r, metal, limit: 8);
            // «СКВОЗЬ КАМЕНЬ НЕ СМОТРЮ» БЫЛО НЕПРАВДОЙ при включённом чите:
            // ответ спрашивает живой флаг, а не пересказывает намерение
            bool сквозь = bot.Prospecting.SeeingThroughStone;
            return a.ReplyAsync(found.Count == 0
                ? $"руды на виду нет (искал в {r} бл; " +
                  (сквозь ? "смотрю сквозь камень — чит включён" : "сквозь камень не смотрю") + ")"
                : string.Join("; ", found.Select(f =>
                    $"{f.Metal} {f.Richness} ({bot.PlayerCoords(f.Pos.X, f.Pos.Y, f.Pos.Z)}, {f.Distance:0} бл)")));
        });

        bot.Commands.Add("жила", "жила [радиус] [металл] — дойти до руды и выработать жилу", async a =>
        {
            var w = a.Words;
            int r = w.Length > 0 && int.TryParse(w[0], out int n) ? n : 24;
            string metal = w.Length > 1 ? w[1] : "";
            await a.ReplyAsync("Иду за рудой…");
            var got = await bot.Ores.MineNearestAsync(r, metal, a.Token);
            await a.ReplyAsync(got.ToString());
        });

        bot.Commands.Add("укройся", "укройся [ниша|закопайся|коробка] — спрятаться от бури", async a =>
        {
            string how = a.Words.Length > 0 ? a.Words[0] : "";
            bool ok = how switch
            {
                "ниша" => await bot.Shelter.DigInAsync(a.Token),
                "закопайся" => await bot.Shelter.BurrowAsync(2, a.Token),
                "коробка" => await bot.Shelter.BuildBoxAsync(a.Token),
                _ => await bot.Shelter.HideAsync(120, a.Token)
            };
            await a.ReplyAsync(ok
                ? $"Укрылся ({bot.PlayerCoordsHere()})"
                : "Укрыться не вышло — причина в журнале");
        });

        bot.Commands.Add("порогголода", "порогголода <доля> — при какой сытости бот садится есть", a =>
        {
            var eat = bot.Behaviors.Get<BehaviorEatWhenHungry>();
            if (eat == null)
                return a.ReplyAsync("способности «есть» нет в списке");
            if (a.Words.Length > 0 && double.TryParse(a.Words[0].Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                eat.HungerThreshold = v;
            return a.ReplyAsync($"Ем при сытости ниже {eat.HungerThreshold:P0}");
        });

        bot.Commands.Add("ешь", "ешь — попытаться поесть сейчас и объяснить, что помешало", async a =>
        {
            var eat = bot.Behaviors.Get<BehaviorEatWhenHungry>();
            if (eat == null)
            {
                await a.ReplyAsync("способности «есть» вообще нет в списке");
                return;
            }
            string sat = bot.Self.Saturation is { } s2 && bot.Self.MaxSaturation is { } m2
                ? $"{s2:0}/{m2:0} ({s2 / Math.Max(1, m2):P0})"
                : "НЕИЗВЕСТНА — сервер не прислал дерево hunger";
            var food = bot.Self.FindBestItem(sl => sl.Code is { } c ? bot.World.GetSatietyByCode(c) : 0);
            await a.ReplyAsync($"Сытость {sat}, порог {eat.HungerThreshold:P0}, " +
                               $"еда в сумках: {food?.Content.Code ?? "нет"}");
            // И честная попытка прямо сейчас, мимо порога и кулдауна
            if (food != null)
            {
                bool ok = await bot.Hands.UseItemAsync(sl => sl.Code == food.Content.Code, 2.5, a.Token);
                await a.ReplyAsync(ok ? "применил — смотри, убыло ли в сумке" : "применить не вышло, причина в журнале");
            }
        });

        // ЦЕПОЧКА «ГДЕ ВЗЯТЬ ЕДУ» — ПО ПРОСЬБЕ И ЦЕЛИКОМ.
        //
        // Зачем команда. Сама цепочка живёт рефлексом и заводится только когда
        // бот проголодался, — а человеку надо ПРОВЕРИТЬ её сейчас: почему бот
        // не идёт охотиться, дошёл ли он до кладовых, есть ли чем готовить.
        // Прежде единственным способом узнать это было ждать голода.
        //
        // ХОДИТ ОНА ТОЙ ЖЕ ДОРОГОЙ, что и рефлекс (BehaviorMeetNeed.QuestNowAsync):
        // тело на каждый способ, успех по факту сумки, отказ со всеми звеньями.
        // Второго прохода по способам в проекте нет — иначе команда однажды
        // показала бы не то, что бот делает на самом деле
        bot.Commands.Add("добудьеду",
            "добудьеду — пройти цепочку «где взять еду» прямо сейчас и назвать каждое звено",
            async a =>
            {
                var руки = bot.Behaviors.Get<BehaviorForageWhenNoFood>();
                if (руки == null)
                {
                    await a.ReplyAsync("способности «сбор еды» нет в списке — цепочку звать некому");
                    return;
                }
                await a.ReplyAsync("иду по цепочке: " +
                                   string.Join(" → ", руки.Ways.Select(w => w.Имя)));
                var отчёт = await руки.QuestNowAsync(a.Token);
                await a.ReplyAsync(отчёт.Reason);
            },
            // ТЕЛО БЕРЁТ САМА ЦЕПОЧКА (needsBody: false), А НЕ ОБЁРТКА КОМАНДЫ.
            //
            // ЖИВОЙ СЛУЧАЙ 26.08, ПОЙМАННЫЙ НА СТЕНДЕ ЭТОЙ ЖЕ КОМАНДОЙ. Обёртка
            // держала тело как «команда добудьеду» (ступень «команда»), а первый
            // же способ берёт его рефлексом — и честно ПЕРЕБИВАЛ команду,
            // которая его позвала:
            //     [тело] «сбор еды: кладовые» важнее — прерываю «команда добудьеду»
            //     [еда] закрыть нечем; пробовал так: кладовые: искать не начал →
            //           готовка: не начинал: тело понадобилось другому делу
            // Цепочка обрывалась на первом же звене, и человек читал это как
            // поломку охоты. Тело у способов своё, по одному на способ, и второй
            // раз просить его сверху нельзя
            needsBody: false);

        bot.Commands.Add("рядом", "рядом <маска> [радиус] — найти блоки по коду вокруг", a =>
        {
            var w = a.Words;
            if (w.Length == 0 || bot.Self.Position is not { } me)
                return a.ReplyAsync("Нужно: !рядом <часть кода> [радиус]");
            string mask = w[0];
            int radius = w.Length > 1 && int.TryParse(w[1], out int r) ? r : 24;
            var here = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
            // КОД БЕРЁМ ТОТ, ЧТО ПОДОШЁЛ, А НЕ СПРАШИВАЕМ СЛОЙ БЛОКОВ ЗАНОВО.
            // Живая улика 27.08 (out/разведка-лук4): «!рядом water 48» ответил
            // списком из «air» — вода нашлась в слое жидкостей, а названа была
            // воздухом, который стоит в слое блоков той же клетки
            var found = bot.World.FindBlocksNamed(
                    (_, c) => c.Contains(mask, StringComparison.OrdinalIgnoreCase),
                    here, radius, height: 40, limit: 400)
                .OrderBy(n => Math.Abs(n.Cell.X - here.X) + Math.Abs(n.Cell.Z - here.Z) +
                              Math.Abs(n.Cell.Y - here.Y))
                .Take(10)
                .Select(n => $"{n.Code} ({bot.PlayerCoords(n.Cell.X, n.Cell.Y, n.Cell.Z)})")
                .ToList();
            return a.ReplyAsync(found.Count == 0
                ? $"«{mask}» в {radius} блоках не вижу"
                : $"Нашёл: {string.Join("; ", found)}");
        });

        bot.Commands.Add("столб", "столб x y z [высота] — что в колонне блоков вверх от точки", a =>
        {
            if (!a.TryGetCellPrefix(out var p))
                return a.ReplyAsync("Нужно: !столб <x> <y> <z> [высота]");
            int h = a.Words.Length > 3 && int.TryParse(a.Words[3], out int hh) ? Math.Clamp(hh, 1, 12) : 5;
            var w = bot.World;
            var lines = new List<string>();
            for (int dy = 0; dy < h; dy++)
            {
                int y = p.Y + dy;
                string code = w.GetBlockCode(p.X, y, p.Z) ?? "?";
                var flags = new List<string>();
                if (w.IsClimbable(p.X, y, p.Z)) flags.Add("лест");
                if (w.IsDoor(p.X, y, p.Z)) flags.Add(w.IsDoorOpen(p.X, y, p.Z) ? "дверь-откр" : "дверь-закр");
                if (w.GetBlockEntity(new BlockPos(p.X, y, p.Z)) != null) flags.Add("BE");
                // ЖИДКОСТЬ ЖИВЁТ В СВОЁМ СЛОЕ, И БЕЗ НЕЁ КОЛОННА ВРЁТ. Разбирая
                // 27.08 затопленный ход у (32, 113, 4), я читал «113: air, 114:
                // air» и целый час считал коридор сухим, — а обе клетки были
                // залиты (saltwater-n-6 и -5). Уровень печатается вместе с
                // кодом: от него игра считает, где тело всплывёт
                // (PModulePlayerInLiquid: (int)Y + LiquidLevel/8 + …)
                if (w.GetLiquidCode(p.X, y, p.Z) is { } жидкость)
                    flags.Add($"{жидкость} залито={w.LiquidFill(p.X, y, p.Z) * 8:0}/8");
                lines.Add($"{y}: {code} верх={w.GetCollisionTop(p.X, y, p.Z):0.##}" +
                          (flags.Count > 0 ? " " + string.Join("/", flags) : ""));
            }
            return a.ReplyAsync(string.Join(" | ", lines));
        });

        bot.Commands.Add("таблички", "таблички [радиус] — что написано на табличках вокруг", a =>
        {
            double r = a.Words.Length > 0 && double.TryParse(a.Words[0], out double rr) ? rr : 12;
            var signs = bot.Read.SignsNear(r);
            if (signs.Count == 0)
                return a.ReplyAsync($"Табличек в радиусе {r:0.#} не вижу");
            return a.ReplyAsync("Таблички: " + string.Join(" | ", signs.Take(5).Select(s =>
                $"({a.Here(s.Pos)}) «{string.Join(" / ", s.Lines)}»" +
                (s.Commands.Any() ? $" [команды: {string.Join(", ", s.Commands)}]" : ""))));
        });

        bot.Commands.Add("сундуки", "сундуки [радиус] — какие контейнеры видно вокруг", a =>
        {
            double r = a.Words.Length > 0 && double.TryParse(a.Words[0], out double rr) ? rr : 12;
            var found = bot.Read.ContainersNear(r);
            if (found.Count == 0)
                return a.ReplyAsync($"Контейнеров в радиусе {r:0.#} не вижу");
            return a.ReplyAsync($"Контейнеров {found.Count}: " + string.Join(", ",
                found.Take(6).Select(c => $"{c.Kind} ({a.Here(c.Pos)})")));
        });

        bot.Commands.Add("осмотри", "осмотри x y z — подойти, открыть контейнер и прочитать содержимое", async a =>
        {
            if (!a.TryGetCellPrefix(out var pos))
            {
                await a.ReplyAsync("Нужно: !осмотри <x> <y> <z>");
                return;
            }
            if (await bot.Read.InspectAsync(pos) is not { } info)
            {
                await a.ReplyAsync($"({a.Here(pos)}) не осмотрел — не подойти или приват");
                return;
            }
            string what = info.Describe();
            await a.ReplyAsync($"{info.Kind} ({a.Here(pos)}): " +
                               (what.Length > 0 ? what : "пусто"));
        });

        bot.Commands.Add("магазин", "что бот считает своим магазином: касса и прилавки", a =>
        {
            var store = shop.StoreSign;
            var cash = shop.CashChest();
            var stalls = shop.Stalls();
            return a.ReplyAsync(
                $"Магазин: {(store == null ? "таблички !STORE не вижу" : $"({a.Here(store.Pos)})")}; " +
                $"касса: {(cash is not { } c ? "не найдена" : $"({a.Here(c)})")}; " +
                $"прилавков {stalls.Count}" +
                (stalls.Count > 0 ? ": " + string.Join(" | ", stalls.Take(5).Select(s =>
                    $"{s.ProductName}→{s.ProductCode ?? "?"} прод.{s.WeSellFor}/пок.{s.WeBuyFor} ({a.Here(s.Chest)})")) : "") +
                $"; складов {shop.Storages().Count}" +
                (shop.Storages().Count > 0 ? ": " + string.Join(", ",
                    shop.Storages().Take(5).Select(s => $"{s.ProductName} ({a.Here(s.Chest)})")) : ""));
        });

        bot.Commands.Add("обход", "обойти прилавки магазина и сказать, где что лежит", async a =>
        {
            var deals = await shop.SurveyAsync();
            if (deals.Count == 0)
            {
                await a.ReplyAsync("Обошёл прилавки — делать нечего");
                return;
            }
            await a.ReplyAsync("Нашёл: " + string.Join(" | ", deals.Select(d => d.Kind switch
            {
                DealKind.BuyFromPlayer => $"{d.Stall.ProductName}: выкупить {d.Units} за {d.Price}",
                DealKind.SellToPlayer => $"{d.Stall.ProductName}: заказ {d.Units} за {d.Price}",
                _ => $"{d.Stall.ProductName}: лишнее ({d.FoundCode})",
            })));
        });

        bot.Commands.Add("торгуй", "полный круг: обойти прилавки и провести все сделки", async a =>
        {
            var deals = await shop.SurveyAsync();
            if (deals.Count == 0)
            {
                await a.ReplyAsync("Обошёл прилавки — сделок нет");
                return;
            }
            foreach (var deal in deals)
                await shop.ExecuteAsync(deal);
            await a.ReplyAsync($"Обработал сделок: {deals.Count}");
        });

        bot.Commands.Add("лечилки", "какие лечебные предметы бот знает и что у него есть", a =>
        {
            // Что бот считает лечилкой — из реестра предметов сервера
            var known = new List<string>();
            foreach (var code in new[]
            {
                "poultice-linen-horsetail", "poultice-linen-honey", "poultice-linen-cattail",
                "bandage-clean", "bandage-dirty"
            })
            {
                if (bot.World.GetHealingByCode(code) is { } h)
                    known.Add($"{code} +{h.Health:0.#}хп за {h.EffectSeconds:0}с");
            }

            var mine = bot.Self.OwnInventories
                .SelectMany(kv => kv.Value.Select(s => s.Code))
                .Where(c => c != null && bot.World.GetHealingByCode(c!) != null)
                .Distinct()
                .ToList();

            return a.ReplyAsync($"Известные лечилки: {(known.Count > 0 ? string.Join("; ", known) : "ни одной не распознал")}. " +
                                $"У меня в инвентаре: {(mine.Count > 0 ? string.Join(", ", mine) : "нет")}");
        });

        bot.Commands.Add("порог", "порог <доля> — при каком здоровье лечиться (0.5 = при половине)", a =>
        {
            var heal = bot.Behaviors.Get<BehaviorHealWhenHurt>();
            if (heal == null)
                return a.ReplyAsync("Способность лечения не подключена");
            if (a.Words.Length != 1 || !ChatCommandArgs.TryParseCoord(a.Words[0], out double v))
                return a.ReplyAsync($"Сейчас порог {heal.HealthThreshold:0.##}. Нужно: !порог <доля>");
            heal.HealthThreshold = v;
            return a.ReplyAsync($"Буду лечиться при здоровье ниже {v:P0}");
        });

        bot.Commands.Add("лечись", "принудительно применить лечилку и следить за здоровьем", async a =>
        {
            var found = bot.Self.FindBestItem(s =>
                s.Code is { } c && bot.World.GetHealingByCode(c) != null ? 1 : 0);
            if (found == null)
            {
                await a.ReplyAsync("Лечилок в инвентаре нет");
                return;
            }
            var info = bot.World.GetHealingByCode(found.Content.Code!)!;
            await a.ReplyAsync($"Применяю {found.Content.Code} ({found.InventoryId.Split('-')[0]}, слот {found.Slot}), " +
                               $"здоровье {bot.Self.Health:0.#}/{bot.Self.MaxHealth:0.#}");

            await bot.Hands.UseItemAsync(s => s.Code == found.Content.Code, info.ApplySeconds + 0.5);

            var trail = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(2500);
                trail.Add($"{bot.Self.Health:0.#}");
            }
            await a.ReplyAsync($"Здоровье по секундам: {string.Join(" → ", trail)} (лечилка даёт +{info.Health:0.#} за {info.EffectSeconds:0}с)");
        });

        bot.Commands.Add("найди", "найди <маска> — коды предметов и блоков из реестра сервера", a =>
        {
            string mask = a.Text.Trim();
            if (mask.Length < 2)
                return a.ReplyAsync("Нужно: !найди <часть кода>");
            var items = bot.World.SearchItemCodes(mask, 8).ToList();
            var blocks = bot.World.SearchBlockCodes(mask, 10).ToList();
            return a.ReplyAsync($"Предметы: {(items.Count > 0 ? string.Join(", ", items) : "нет")}. " +
                                $"Блоки: {(blocks.Count > 0 ? string.Join(", ", blocks) : "нет")}");
        });

        bot.Commands.Add("врюкзак", "врюкзак <часть кода> — убрать предмет из хотбара в рюкзак (проверка снаряжения)", async a =>
        {
            string mask = a.Text.Trim();
            var hotbar = bot.Self.GetInventory("hotbar");
            string? hotbarId = bot.Self.GetInventoryId("hotbar");
            string? backpackId = bot.Self.GetInventoryId("backpack");
            var backpack = bot.Self.GetInventory("backpack");
            if (hotbar == null || hotbarId == null || backpackId == null || backpack == null)
            {
                await a.ReplyAsync("Не вижу своих инвентарей");
                return;
            }
            int from = Array.FindIndex(hotbar, s => s.Code?.Contains(mask, StringComparison.OrdinalIgnoreCase) == true);
            // Первые четыре слота — места ПОД сумки, вещи туда не кладут
            int to = Array.FindIndex(backpack, 4, s => s.IsEmpty);
            if (from < 0 || to < 0)
            {
                await a.ReplyAsync($"Не нашёл \"{mask}\" в хотбаре или нет места в рюкзаке");
                return;
            }
            string code = hotbar[from].Code!;
            await bot.Actions.MoveItemAsync(hotbarId, from, backpackId, to, hotbar[from].Count);
            await a.ReplyAsync($"Убрал {code} в рюкзак (слот {to}) — посмотрим, вернёт ли бот его в руки");
        });

        bot.Commands.Add("инвентари", "что лежит во всех моих инвентарях", a =>
        {
            var parts = bot.Self.OwnInventories.Select(kv =>
            {
                var items = kv.Value.Where(s => !s.IsEmpty).Select(s => s.ToString()).ToList();
                return $"{kv.Key.Split('-')[0]}({kv.Value.Length}): {(items.Count > 0 ? string.Join(", ", items) : "пусто")}";
            });
            return a.ReplyAsync(string.Join(" | ", parts));
        });

        bot.Commands.Add("путь", "путь x z | путь x y z — построить маршрут без движения", a =>
        {
            if (bot.Self.Position is not { } sp)
                return a.ReplyAsync("Не знаю, где я — маршрут строить не от чего");
            // ТРИ ЧИСЛА — ЭТО КЛЕТКА (x y z), ДВА — ТОЛЬКО x и z. Разбор их
            // нарочно разный: молча выбросить названную высоту нельзя, бот
            // ушёл бы не туда (см. ChatCommandArgs.TryGetCoords)
            BlockPos to;
            if (a.Words.Length >= 3)
            {
                if (!a.TryGetCell(out var клетка))
                    return a.ReplyAsync("Нужно: !путь <x> <z> или !путь <x> <y> <z>");
                to = клетка;
            }
            else if (a.TryGetCoords(out double px, out double pz))
                to = new BlockPos((int)px, (int)sp.Y, (int)pz);
            else
                return a.ReplyAsync("Нужно: !путь <x> <z> или !путь <x> <y> <z>");

            var opt = bot.Movement.PathOptions.Clone();
            opt.FallDamageBudget = bot.Movement.MaxFallDamage;
            var from = new BlockPos((int)sp.X, (int)sp.Y, (int)sp.Z);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var path = bot.Movement.PathFinder.FindPath(bot.World, from, to, opt);
            sw.Stop();

            if (path == null)
                return a.ReplyAsync($"Путь {from} -> {to} не найден ({sw.ElapsedMilliseconds} мс)");

            int falls = 0, swims = 0;
            for (int i = 1; i < path.Count; i++)
            {
                if (path[i - 1].Y - path[i].Y > bot.Movement.SafeStepDown) falls++;
                if (bot.World.IsWaterSurface(path[i].X, path[i].Y, path[i].Z)) swims++;
            }

            // Прыжки считаем по НЕСПРЯМЛЁННОМУ маршруту: после спрямления
            // прямой участок в тридцать блоков выглядит одним шагом, и наивный
            // счётчик объявляет его прыжком на тридцать клеток
            int jumps = 0, longest = 0;
            var raw = opt.Clone();
            raw.SmoothPath = false;
            if (bot.Movement.PathFinder.FindPath(bot.World, from, to, raw) is { } detailed)
                for (int i = 1; i < detailed.Count; i++)
                {
                    int span = Math.Max(Math.Abs(detailed[i].X - detailed[i - 1].X),
                                        Math.Abs(detailed[i].Z - detailed[i - 1].Z));
                    if (span > 1) { jumps++; longest = Math.Max(longest, span); }
                }
            // САМАЯ ВЫСОКАЯ ТОЧКА МАРШРУТА И ВЫСОТА НОГ НА НЕЙ. Без этого
            // числа живой прогон не мог показать, зовёт ли поиск тело выше
            // линии плавучести: «Пришёл» меряется по горизонтали
            var верхняя = path[0].Pos;
            foreach (var шаг in path)
                if (шаг.Pos.Y > верхняя.Y)
                    верхняя = шаг.Pos;
            return a.ReplyAsync($"Путь {from} -> {to}: {path.Count} точек за {sw.ElapsedMilliseconds} мс, " +
                                $"прыжков {jumps} (самый длинный {longest} кл), " +
                                $"спрыгиваний {falls}, точек по воде {swims}, " +
                                $"самая высокая точка {верхняя} " +
                                $"(ноги там на {bot.World.FeetY(верхняя.X, верхняя.Y, верхняя.Z):0.##})");
        });

        bot.Commands.Add("клетка", "клетка x y z [лестница] — дойти до клетки; 'лестница' запрещает падения", async a =>
        {
            if (a.Words.Length is < 3 or > 4 || !a.TryGetCellPrefix(out var goal))
            {
                await a.ReplyAsync("Нужно: !клетка <x> <y> <z> [лестница]");
                return;
            }
            var opts = bot.Movement.PathOptions;
            bool noFalls = a.Words.Length == 4;
            int savedDrop = opts.SafeDropDistance, savedFall = opts.MaxDeliberateFall;
            if (noFalls)
            {
                opts.SafeDropDistance = 1;
                opts.MaxDeliberateFall = 0;
            }
            try
            {
                bool ok = await bot.Movement.MoveToCellAsync(goal, a.Token);
                var now = bot.Self.Position;
                // Поиск пути подтягивает цель к настоящей опоре в колонне —
                // честно говорим, если по высоте вышли не туда, куда просили
                string height = now != null && Math.Abs(now.Value.Y - goal.Y) > 1.2
                    ? $" — но по высоте не туда: просили {goal.Y}, стою на {now.Value.Y:0.#}"
                    : "";
                await a.ReplyAsync($"{(ok ? "Дошёл" : "Не дошёл")} до ({a.Here(goal)}): " +
                                   (now is { } где
                                       ? WorldOrigin.Say(где.X, где.Y, где.Z)
                                       : "где я — не знаю") + height);
            }
            finally
            {
                opts.SafeDropDistance = savedDrop;
                opts.MaxDeliberateFall = savedFall;
            }
        });

        // ИМЯ «дорога» ОТДАНО МОЩЕНИЮ. Живой случай 23:38: на «!дорога 323 113
        // -258» отработала вот эта команда-поход (отладка перекрывает навык
        // роли), бот полчаса пытался ДОЙТИ, и в журнале не было ни одного
        // положенного блока. Поход — это «дойди», мощение — «дорога»
        bot.Commands.Add("дойди", "дойди x y z — дальний поход (цель может быть за краем прогруженного мира)", async a =>
        {
            if (!a.TryGetCell(out var goal))
            {
                await a.ReplyAsync("Нужно: !дойди <x> <y> <z>");
                return;
            }
            var started = bot.Self.Position;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = await bot.Movement.TravelToAsync(goal, 300, a.Token);
            sw.Stop();
            var now = bot.Self.Position;
            // «Где я сейчас» и «где был» — ИГРОВЫМИ числами: ответ уходит в чат
            // человеку, а он сверяет его с окном координат игры. Раньше здесь
            // печатались сырые поля позиции, и ответ на «!дойди» называл место
            // за полмиллиона блоков от того, что человек видел у себя
            string Место((double X, double Y, double Z)? p) =>
                p is { } q ? WorldOrigin.Say(q.X, q.Y, q.Z) : "неизвестно где";
            await a.ReplyAsync($"{(ok ? "Дошёл" : "Не дошёл")} до ({a.Here(goal)}) за {sw.Elapsed.TotalSeconds:0} с: " +
                               $"{Место(now)}, было {Место(started)}");
        });

        bot.Commands.Add("транслокаторы", "какие транслокаторы бот видел и куда они ведут", a =>
        {
            var all = bot.Translocators.All.ToList();
            // ПАМЯТЬ НАЗЫВАЕМ ВСЕГДА, даже когда живых транслокаторов рядом нет.
            // «Транслокаторов пока не видел» при полной памяти пар читалось бы
            // как «я про них ничего не знаю», а это неправда: видимое — только
            // то, чьи чанки лежат в памяти прямо сейчас, а помнит бот больше
            string память = bot.Translocations.Tell();
            if (all.Count == 0)
                return a.ReplyAsync($"Сейчас рядом транслокаторов не вижу. {память}");
            var p = bot.Self.Position;
            var near = all
                .OrderBy(t => p == null ? 0 : Math.Sqrt(Math.Pow(t.Pos.X - p.Value.X, 2) + Math.Pow(t.Pos.Z - p.Value.Z, 2)))
                .Take(4);
            return a.ReplyAsync($"Вижу {all.Count} шт., рабочих {all.Count(t => t.Working)}. Ближайшие: " +
                                string.Join("; ", near) + $". {память}");
        });

        // МАРШРУТ ПО СЕТИ ПЕРЕХОДОВ — единственное живое место, откуда счёт по
        // карте сервера вообще спрашивается. Пока команды не было, весь граф
        // (чтение GeoJSON, Дейкстра, цены) звался только из тестов: человек,
        // вписавший адрес карты, получал картинку и НИ ОДНОГО маршрута, и
        // причины ему никто не называл
        bot.Commands.Add("маршрут",
            "маршрут <x> <y> <z> — путь до точки по сети транслокаторов (координаты игровые)", a =>
            {
                if (!a.TryGetCell(out var цель))
                    return a.ReplyAsync("Нужно: !маршрут <x> <y> <z> (координаты как в игре, " +
                                        "с «=» — абсолютные мировые)");
                if (bot.Self.Position is not { } я)
                    return a.ReplyAsync("Не знаю, где я — маршрут строить не от чего");

                var откуда = new BlockPos((int)Math.Floor(я.X), (int)Math.Floor(я.Y),
                    (int)Math.Floor(я.Z));
                var путь = bot.Routes.RouteBetween(откуда, цель);
                // Отказ у маршрута всегда со словами — их и отдаём человеку
                // целиком: «сети взять неоткуда, впишите адрес карты» полезнее,
                // чем «маршрута нет»
                return a.ReplyAsync(путь.TellSteps());
            });

        bot.Commands.Add("перенесись", "перенесись [x y z] — дойти до транслокатора и перенестись", async a =>
        {
            TranslocatorInfo? target;
            if (a.Words.Length == 3 &&
                ChatCommandArgs.TryParseCoord(a.Words[0], out double lx) &&
                ChatCommandArgs.TryParseCoord(a.Words[1], out double ly) &&
                ChatCommandArgs.TryParseCoord(a.Words[2], out double lz))
            {
                target = bot.Translocators.At(new BlockPos((int)lx, (int)ly, (int)lz));
                if (target == null)
                {
                    await a.ReplyAsync("В этой клетке транслокатора не вижу");
                    return;
                }
            }
            else
            {
                if (bot.Self.Position is not { } sp)
                    return;
                target = bot.Translocators.Nearest(sp.X, sp.Y, sp.Z);
                if (target == null)
                {
                    await a.ReplyAsync($"Рабочих транслокаторов не знаю (всего видел {bot.Translocators.All.Count()})");
                    return;
                }
            }

            // СЛОВО СЕТИ ПРОТИВ СЛОВА СЕРВЕРА — спрашиваем ДО похода. Мнения
            // может и не быть (сети нет, сдвиг не известен, конца в ней нет) —
            // тогда идём без него; а вот если есть и оно расходится с
            // блок-сущностью, идти нельзя: шаг рассчитан на другой выход.
            // «Сеть» — это карта сервера И своя память бота вместе
            var поСети = bot.Routes.MapExitFor(target.Pos);
            await a.ReplyAsync($"Иду к {target}" +
                               (поСети is { } м ? $" (сеть обещает выход {a.Here(м)})" : ""));
            var result = await TranslocatorMap.TravelThroughAsync(bot.Context, target,
                promisedExit: поСети);
            await a.ReplyAsync(result.Message ?? (result.Success ? "Готово" : "Не вышло"));
        });

        // Только для стендов: строит не бот, а worldedit от его имени
        bot.Commands.Add("стенд", "стенд <код блока> [x y z] [грань] — поставить блок креативом (для тестов)", async a =>
        {
            if (a.Words.Length is not (1 or 4 or 5))
            {
                await a.ReplyAsync("Нужно: !стенд <код блока> [x y z] [грань 0..5]");
                return;
            }
            // Грань важна для лестниц: она задаёт, к какой стене крепиться
            int face = 4;
            if (a.Words.Length == 5 && ChatCommandArgs.TryParseCoord(a.Words[4], out double f))
                face = Math.Clamp((int)f, 0, 5);
            string code = a.Words[0];
            var where = new BlockPos(0, 0, 0);
            if (a.Words.Length >= 4)
            {
                // Стенды строим по абсолютным мировым координатам — так проще
                // сверяться с тем, что бот видит внутри себя
                if (!ChatCommandArgs.TryParseCoord(a.Words[1].TrimStart('='), out double wx) ||
                    !ChatCommandArgs.TryParseCoord(a.Words[2].TrimStart('='), out double wy) ||
                    !ChatCommandArgs.TryParseCoord(a.Words[3].TrimStart('='), out double wz))
                {
                    await a.ReplyAsync("Координаты не разобрал: !стенд <код блока> <x> <y> <z> [грань]");
                    return;
                }
                where = new BlockPos((int)wx, (int)wy, (int)wz);
            }
            // ОТКАЗ ДО ПЕРВОЙ КОМАНДЫ, А НЕ МОЛЧАЛИВЫЙ ХОЛОСТОЙ ПРОГОН: право
            // спрашивается у того же единственного источника, что и у страховки
            // при входе (ModeInsurance), второго способа узнать это не заведено
            if (ModeInsurance.WhyStandCannotRun(
                    bot.Self.May(Vintagestory.API.Server.Privilege.gamemode)) is { } нельзя)
            {
                await a.ReplyAsync(нельзя);
                return;
            }
            // Команды стенда идут строго по одной: они переключают режим и
            // телепортируют бота, вперемешку это превращается в кашу
            await standLock.WaitAsync();
            // Гравитация уронила бы бота с точки установки — на время выключаем
            var земля = bot.Behaviors.Get<BehaviorStayGrounded>();
            if (земля != null)
                bot.Behaviors.Remove(земля);
            try
            {
                await bot.SayAsync("/gamemode 2");
                await bot.SayAsync($"/giveblock {code} 1");
                await bot.SayAsync("/we on");
                await Task.Delay(2500);

                // Ставим через worldedit: он не проверяет опору, поэтому можно
                // повесить лестницу в воздухе — обычная установка так не умеет
                var hotbar = bot.Self.GetInventory("hotbar");
                int slot = -1;
                for (int i = 0; hotbar != null && i < hotbar.Length && i < 10; i++)
                    if (hotbar[i].Code is { } c && c.Contains(code.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                        slot = i;
                if (slot < 0)
                {
                    await a.ReplyAsync($"«{code}» в хотбаре не нашёл — не выдался?");
                    return;
                }
                await bot.Actions.SelectHotbarSlotAsync(slot);
                await Task.Delay(400);

                if (a.Words.Length >= 4)
                {
                    // /we block ставит блок ПОД вызывающим — встаём на клетку выше
                    await bot.SayAsync($"/tp ={where.X} ={where.Y + 1} ={where.Z}");
                    await Task.Delay(2000);
                }
                else if (bot.Self.Position is { } p)
                {
                    where = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y) - 1, (int)Math.Floor(p.Z));
                }
                await bot.SayAsync("/we block");
                await Task.Delay(1500);

                string got = bot.World.GetBlockCode(where.X, where.Y, where.Z) ?? "?";
                await a.ReplyAsync($"В ({a.Here(where)}) теперь {got}" +
                                   (got.Contains(code.Split('-')[0], StringComparison.OrdinalIgnoreCase)
                                       ? "" : " — установка НЕ прошла"));
            }
            finally
            {
                // Бот живёт в выживании: креатив и worldedit — только на время стенда
                await bot.SayAsync("/we off");
                await bot.SayAsync("/gamemode survival");
                if (земля != null)
                    bot.Behaviors.Add(земля);
                standLock.Release();
            }
        });

        bot.Commands.Add("вруку", "вруку <часть кода> — взять предмет из хотбара в активный слот", async a =>
        {
            var hotbar = bot.Self.GetInventory("hotbar");
            if (a.Words.Length != 1 || hotbar == null)
            {
                await a.ReplyAsync("Нужно: !вруку <часть кода>");
                return;
            }
            for (int i = 0; i < hotbar.Length && i < 10; i++)
            {
                if (hotbar[i].Code is not { } c ||
                    !c.Contains(a.Words[0], StringComparison.OrdinalIgnoreCase))
                    continue;
                await bot.Actions.SelectHotbarSlotAsync(i);
                await a.ReplyAsync($"Взял в руку {c} (слот {i})");
                return;
            }
            await a.ReplyAsync($"«{a.Words[0]}» в хотбаре нет: " +
                               string.Join(", ", hotbar.Take(10).Select(s => s.Code ?? "-")));
        });

        bot.Commands.Add("потыкай", "потыкай x y z [раз] — нажать правой кнопкой по блоку (проверка взаимодействий)", async a =>
        {
            if (a.Words.Length is not (3 or 4) ||
                !ChatCommandArgs.TryParseCoord(a.Words[0], out double ux) ||
                !ChatCommandArgs.TryParseCoord(a.Words[1], out double uy) ||
                !ChatCommandArgs.TryParseCoord(a.Words[2], out double uz))
            {
                await a.ReplyAsync("Нужно: !потыкай <x> <y> <z> [раз]");
                return;
            }
            int times = a.Words.Length == 4 && ChatCommandArgs.TryParseCoord(a.Words[3], out double t)
                ? Math.Clamp((int)t, 1, 10) : 1;
            var target = new BlockPos((int)ux, (int)uy, (int)uz);

            for (int i = 0; i < times; i++)
            {
                await bot.Actions.UseBlockAsync(target);
                await Task.Delay(700);
            }
            await a.ReplyAsync($"Нажал {times} раз(а) по ({a.Here(target)}) " +
                               $"({bot.World.GetBlockCode(target.X, target.Y, target.Z) ?? "?"})");
        });

        bot.Commands.Add("ищиблок", "ищиблок <часть кода> [радиус] — найти блок в прогруженной округе", a =>
        {
            if (a.Words.Length is < 1 or > 2 || bot.Self.Position is not { } bp)
                return a.ReplyAsync("Нужно: !ищиблок <часть кода> [радиус]");
            string mask = a.Words[0];
            int radius = a.Words.Length == 2 && ChatCommandArgs.TryParseCoord(a.Words[1], out double r)
                ? Math.Clamp((int)r, 1, 96) : 48;
            int cx = (int)Math.Floor(bp.X), cy = (int)Math.Floor(bp.Y), cz = (int)Math.Floor(bp.Z);

            // ОБХОД ОДИН НА ВЕСЬ ПРОЕКТ (WorldModel.FindBlocksNamed), А НЕ СВОЙ.
            //
            // ЖИВАЯ БЕДА 27.08. Здесь стоял СОБСТВЕННЫЙ тройной цикл по
            // GetBlockCode — вторая копия механики поиска (закон 2). Когда поиск
            // научился видеть слой ЖИДКОСТЕЙ, эта копия осталась слепой: в один
            // и тот же прогон «!рядом water 48» находил воду, а «!ищиблок water
            // 96» отвечал «не вижу», стоя в этой самой воде. Две волны подряд
            // писали в отчёт «воды в мире нет» именно по ответу «!ищиблок».
            //
            // Обход колец идёт ОТ ЦЕНТРА, поэтому первым находится ближайшее —
            // прежний цикл шёл сверху вниз и называл дальнее, если оно выше
            var found = bot.World.FindBlocksNamed(
                    (_, code) => code.Contains(mask, StringComparison.OrdinalIgnoreCase),
                    new BlockPos(cx, cy, cz), radius, height: 48, limit: 6,
                    throughWalls: true)
                .Select(n =>
                {
                    double d = Math.Sqrt(
                        (double)(n.Cell.X - cx) * (n.Cell.X - cx) +
                        (double)(n.Cell.Y - cy) * (n.Cell.Y - cy) +
                        (double)(n.Cell.Z - cz) * (n.Cell.Z - cz));
                    return $"{n.Code} в ({a.Here(n.Cell.X, n.Cell.Y, n.Cell.Z)}) — {d:0} бл";
                })
                .ToList();
            return a.ReplyAsync(found.Count > 0
                ? string.Join("; ", found)
                : $"Блоков по маске «{mask}» в радиусе {radius} не вижу");
        });

        bot.Commands.Add("глубоко", "глубоко [радиус] — найти воду глубиной 2+ (для проверки плавания)", a =>
        {
            if (bot.Self.Position is not { } dp)
                return a.ReplyAsync("Себя не вижу");
            int radius = a.Words.Length == 1 && ChatCommandArgs.TryParseCoord(a.Words[0], out double r)
                ? Math.Clamp((int)r, 8, 120) : 60;
            int cx = (int)dp.X, cy = (int)dp.Y, cz = (int)dp.Z;
            for (int dx = -radius; dx <= radius; dx += 2)
            for (int dz = -radius; dz <= radius; dz += 2)
            for (int dy = 8; dy >= -20; dy--)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (bot.World.IsWaterSurface(x, y, z) && bot.World.IsWater(x, y - 1, z) &&
                    bot.World.IsWater(x, y - 2, z))
                    return a.ReplyAsync($"Глубокая вода: ({a.Here(x, y, z)}), глубина 3+ " +
                                        $"(абс. {x} {y} {z})");
            }
            return a.ReplyAsync($"Воды глубже 2 блоков в радиусе {radius} не нашёл");
        });

        bot.Commands.Add("телом", "телом <блоков> — пройти вперёд НА ИГРОВОЙ ФИЗИКЕ (новое ядро)", async a =>
        {
            if (bot.Body.Physics is not { } phys)
            {
                await a.ReplyAsync("Тело ещё не на игровой физике (ждём реестр блоков)");
                return;
            }
            double startX = phys.X, startZ = phys.Z, startY = phys.Y;
            double goalX, goalZ;
            if (a.TryGetCoords(out double tx, out double tz))
            {
                goalX = tx;
                goalZ = tz;
            }
            else
            {
                // Без цели — по направлению взгляда на 10 блоков
                goalX = startX + Math.Sin(phys.Yaw) * 10;
                goalZ = startZ + Math.Cos(phys.Yaw) * 10;
            }
            double blocks = Math.Sqrt(Math.Pow(goalX - startX, 2) + Math.Pow(goalZ - startZ, 2));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var trail = new List<(double T, double X, double Z)>();
            while (sw.Elapsed.TotalSeconds < 30)
            {
                double left = Math.Sqrt(Math.Pow(goalX - phys.X, 2) + Math.Pow(goalZ - phys.Z, 2));
                if (left <= 0.5)
                    break;
                phys.WalkToward(goalX, goalZ, sprint: left > 6);
                trail.Add((sw.Elapsed.TotalSeconds, phys.X, phys.Z));
                await Task.Delay(50);
            }
            phys.Stop();
            double walked = Math.Sqrt(Math.Pow(phys.X - startX, 2) + Math.Pow(phys.Z - startZ, 2));

            // Чистая скорость: лучшее секундное окно (без стояния у стен)
            double best = 0;
            for (int i = 0; i < trail.Count; i++)
            for (int j = i + 1; j < trail.Count; j++)
            {
                double t = trail[j].T - trail[i].T;
                if (t < 0.9) continue;
                if (t > 1.2) break;
                best = Math.Max(best, Math.Sqrt(Math.Pow(trail[j].X - trail[i].X, 2) +
                                                Math.Pow(trail[j].Z - trail[i].Z, 2)) / t);
            }
            await a.ReplyAsync($"Прошёл телом {walked:0.##} из {blocks:0.#} бл за {sw.Elapsed.TotalSeconds:0.#} с " +
                               $"(пик {best:0.##} бл/с), " +
                               $"высота {startY:0.#} → {phys.Y:0.#}, на земле: {phys.OnGround}, " +
                               $"лезу: {phys.Climbing}, плыву: {phys.Swimming}, ноги в воде: {phys.FeetInLiquid}, " +
                               $"в стену: {phys.CollidedHorizontally}, motion=({phys.Motion.X:0.####}, {phys.Motion.Y:0.####}, {phys.Motion.Z:0.####})");
        });

        bot.Commands.Add("физика", "прогнать игровую физику на месте бота (проверка ядра)", a =>
        {
            if (bot.Self.Position is not { } fp)
                return a.ReplyAsync("Себя не вижу");
            if (!bot.World.GameBlocksReady)
                return a.ReplyAsync("Реестр блоков игры ещё не собран");

            try
            {
                var phys = new BotGamePhysics.GamePlayerPhysics(bot.World);
                phys.Teleport(fp.X, fp.Y + 2, fp.Z);   // подвесим на 2 блока — должен упасть
                var start = (phys.X, phys.Y, phys.Z);

                for (int i = 0; i < 60; i++)
                    phys.Tick();
                var afterFall = phys.Y;

                phys.Controls.Forward = true;
                phys.Yaw = 0;
                for (int i = 0; i < 60; i++)
                    phys.Tick();

                double walked = Math.Sqrt(Math.Pow(phys.X - start.X, 2) + Math.Pow(phys.Z - start.Z, 2));
                return a.ReplyAsync(
                    $"Ядро игры работает: с {start.Y:0.##} упал на {afterFall:0.##} (на земле: {phys.OnGround}), " +
                    $"за секунду прошёл {walked:0.##} бл, плыву: {phys.Swimming}");
            }
            catch (Exception ex)
            {
                return a.ReplyAsync($"Физика упала: {ex.GetType().Name} {ex.Message}");
            }
        });

        bot.Commands.Add("скорость", "какая у меня скорость и откуда она берётся", a =>
        {
            if (bot.Entities.Self is not { } self || bot.Self.Position is not { } sp)
                return a.ReplyAsync("Себя ещё не вижу");
            // ЗЕМЛЮ СПРАШИВАЕМ ТЕМ ЖЕ ОДНИМ ВХОДОМ, ЧТО И САМ ХОД
            // (WorldModel.WalkSpeedAt → GroundSpeed). Здесь стояла своя копия
            // половины игрового правила — «блок в 0,05 ниже ног», один, — и она
            // расходилась с Movement.WalkSpeed ровно там, где заказчику и было
            // больно: на снегу. Слой без коробки коллизии (snowlayer-1) она не
            // видела вовсе, а под сугробом показывала закопанную каменную тропу
            // с её 1,3, пока бот шёл со скоростью 0,65. Команда «!скорость»
            // заведена, чтобы ОБЪЯСНЯТЬ скорость, — а объясняла она другую
            float ground = bot.World.WalkSpeedAt(sp.X, sp.Y, sp.Z);
            return a.ReplyAsync($"Стат walkspeed {self.WalkSpeedStat:0.###} (класс + снаряжение), " +
                                $"земля под ногами ×{ground:0.##} → " +
                                $"шаг {bot.Movement.WalkSpeed:0.##} бл/с, " +
                                $"бег {bot.Movement.WalkSpeed * bot.Movement.SprintMultiplier:0.##} бл/с");
        });

        // ЧТО ИМЕННО ЛЕЖИТ В СУМКАХ — одной строкой, с человеческими именами
        // предметов (их бот берёт у файлов установленной игры, сервер шлёт
        // только коды). Не путать с «!сумки»: та НАДЕВАЕТ сумки, а эта
        // рассказывает, что в них. Курсор показывается нарочно: висящая на нём
        // стопка однажды стоила вечера — склад доложил «легло 112 шт.», а
        // сундук был пуст
        bot.Commands.Add("сумка", "что у меня в сумках", async a =>
            await a.ReplyAsync(Bags.Line(Bags.Snapshot(bot.Context))));

        bot.Commands.Add("сумки", "надеть сумки из инвентаря (без них всего 10 слотов)", async a =>
        {
            int worn = await ArmorKit.EquipBagsAsync(bot.Context);
            var bags = bot.Self.GetInventory("backpack");
            string list = bags == null
                ? "инвентарь сумок не пришёл"
                : string.Join(", ", bags.Take(4).Select((b, i) => $"{i}: {b.Code ?? "—"}"));
            await a.ReplyAsync($"Надел сумок: {worn}. Слоты сумок — {list}. " +
                               $"Всего мест под вещи: {(bags?.Length ?? 4) - 4 + 10}");
        });

        bot.Commands.Add("броня", "броня [надень|сними] — что надето и что есть в сумках", async a =>
        {
            if (a.Words.Length == 1 && a.Words[0].StartsWith("над"))
                await a.ReplyAsync($"Надел предметов: {await ArmorKit.EquipBestAsync(bot.Context)}");
            else if (a.Words.Length == 1 && a.Words[0].StartsWith("сни"))
                await a.ReplyAsync($"Снял предметов: {await ArmorKit.UnequipAllAsync(bot.Context)}");

            var worn = ArmorKit.Worn(bot.Context).ToList();
            var inBags = bot.Self.OwnInventories
                .Where(kv => !kv.Key.StartsWith("character") && !kv.Key.StartsWith("mouse"))
                .SelectMany(kv => kv.Value)
                .Where(s => s.Code != null && bot.World.GetArmorByCode(s.Code) != null)
                .Select(s => $"{s.Code} ({bot.World.GetArmorByCode(s.Code!)})")
                .ToList();
            await a.ReplyAsync($"Надето: {(worn.Count > 0 ? string.Join("; ", worn.Select(w => $"{w.Code} [{w.Info}]")) : "ничего")}. " +
                               $"В сумках: {(inBags.Count > 0 ? string.Join("; ", inBags.Take(4)) : "брони нет")}");
        });

        bot.Commands.Add("земля", "земля x z — на какой высоте пол в этой колонне", a =>
        {
            if (!a.TryGetCoords(out double gx, out double gz))
                return a.ReplyAsync("Нужно: !земля <x> <z>");
            int x = (int)gx, z = (int)gz;
            for (int y = 200; y > 20; y--)
                if (bot.World.IsStandable(x, y, z))
                    return a.ReplyAsync($"Пол в ({x}, {z}) на высоте {y} " +
                                        $"(блок под ногами: {bot.World.GetBlockCode(x, y - 1, z) ?? "?"})");
            return a.ReplyAsync($"В колонне ({x}, {z}) опоры не нашёл — чанк не прогружен?");
        });

        bot.Commands.Add("бой", "бой [порог] — настройки самообороны; порог = с какого урона предмет считается оружием", a =>
        {
            var fight = bot.Behaviors.Get<BehaviorAttackHostiles>();
            if (fight == null)
                return a.ReplyAsync("Способность самообороны не подключена");
            string сказать = "";
            // ПОРОГ ОРУЖИЯ ручки роли не имеет НИ У КОГО — и это не забывчивость:
            // «с какого урона предмет считается оружием» меряется реестром игры,
            // а не вкусом хозяина. Стирать сказанное тут некому (роль доносит
            // только свои ручки), но и записано оно нигде — про это и говорим.
            //
            // ХОЗЯИН У ЧИСЛА ОДИН, и это важнее всего сказанного выше. Порог
            // спрашивают ДВОЕ — самооборона и сбор оружия к ночи, — и пока у
            // каждой была своя копия, эта строка правила половину: бот отвечал
            // «оружием считаю от 3 урона», а к темноте брал в руку предмет с
            // ценностью 2 и шёл с ним в ночь. Теперь запись идёт в общую мерку
            // (BotContext.Weapons), и обе стороны видят одно число
            if (a.Words.Length == 1 && ChatCommandArgs.TryParseCoord(a.Words[0], out double th))
                сказать += ЧерезРоль(bot, "ПорогОружия", th, () => fight.MinWeaponPower = (float)th);
            // А вот «отступаю ниже» — ручка роли («ОтступатьНижеЗдоровья»), и
            // писать мимо неё нельзя: роль доносит её заново на каждый поворот
            // соседнего ползунка и стёрла бы сказанное молча
            if (a.Words.Length == 2 && a.Words[0].StartsWith("отступ") &&
                ChatCommandArgs.TryParseCoord(a.Words[1], out double rt))
                сказать += ЧерезРоль(bot, "ОтступатьНижеЗдоровья", rt,
                    () => fight.RetreatHealthFraction = rt);
            return a.ReplyAsync($"Самооборона: радиус {fight.DefendRadius:0.#}, при уроне {fight.HurtResponseRadius:0.#}, " +
                                $"оружием считаю от {fight.MinWeaponPower:0.##} урона " +
                                "(и в бою, и когда беру оружие к ночи — мерка одна), " +
                                $"без оружия {(fight.FleeIfUnarmed ? "убегаю" : "дерусь как есть")}, " +
                                $"отступаю ниже {fight.RetreatHealthFraction:P0} здоровья, " +
                                $"возвращаюсь с {fight.ResumeHealthFraction:P0}." + сказать);
        });

        bot.Commands.Add("обрыв", "найти уступ по силам боту", a =>
        {
            if (bot.Self.Position is not { } op)
                return a.ReplyAsync("Не знаю своей позиции");
            int cx = (int)op.X, cy = (int)op.Y, cz = (int)op.Z;
            (BlockPos top, BlockPos bottom, int drop)? best = null;
            for (int dx = -40; dx <= 40; dx++)
            for (int dz = -40; dz <= 40; dz++)
            {
                int? topY = bot.World.FindStandableY(cx + dx, cy, cz + dz, up: 12, down: 12);
                if (topY == null) continue;
                foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int? lowY = bot.World.FindStandableY(cx + dx + ox, topY.Value, cz + dz + oz, up: 0, down: 14);
                    if (lowY == null) continue;
                    int drop = topY.Value - lowY.Value;
                    if (drop >= 4 && AStarPathFinder.EstimateFallDamage(drop) <= bot.Movement.MaxFallDamage &&
                        (best == null || drop > best.Value.drop))
                        best = (new BlockPos(cx + dx, topY.Value, cz + dz),
                                new BlockPos(cx + dx + ox, lowY.Value, cz + dz + oz), drop);
                }
            }
            return a.ReplyAsync(best is { } b
                ? $"Уступ: сверху ({a.Here(b.top)}), снизу ({a.Here(b.bottom)}), перепад {b.drop} бл (~{AStarPathFinder.EstimateFallDamage(b.drop):0.#} хп)"
                : "Подходящих уступов рядом нет");
        });

        bot.Commands.Add("вода", "видит ли бот воду и где ближайшая", a =>
        {
            if (bot.Self.Position is not { } wp)
                return a.ReplyAsync("Не знаю своей позиции");
            int cx = (int)wp.X, cy = (int)wp.Y, cz = (int)wp.Z;
            int cells = 0;
            (BlockPos pos, double dist)? nearest = null;
            for (int dx = -50; dx <= 50; dx++)
            for (int dz = -50; dz <= 50; dz++)
            for (int dy = -20; dy <= 10; dy++)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (!bot.World.IsWater(x, y, z)) continue;
                cells++;
                if (!bot.World.IsWaterSurface(x, y, z)) continue;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (nearest == null || d < nearest.Value.dist)
                    nearest = (new BlockPos(x, y, z), d);
            }
            return a.ReplyAsync(nearest is { } n
                ? $"Воды вокруг: {cells} клеток, ближайшая поверхность {n.pos} в {n.dist:0.#} бл"
                : $"Воды не вижу (клеток: {cells})");
        });

        bot.Commands.Add("атакуй", "ударить ближайшее существо (проверка боя)", async a =>
        {
            if (bot.Self.Position is not { } mp) return;
            // ЖИВОЕ И ТОЛЬКО ЖИВОЕ — тем же правилом, что и настоящий бой
            // (CombatTargets.CanBeFought). Без него команда предлагала ударить
            // воткнутую стрелу ровно так же, как это делал сам бой
            var victim = bot.Entities.Nearby(mp.X, mp.Y, mp.Z, 30)
                .FirstOrDefault(e => !e.IsPlayer && e.Id != bot.Entities.OwnEntityId &&
                                     CombatTargets.CanBeFought(
                                         bot.EntityTypes.Inanimate(e.Code), e.Health));
            if (victim == null)
            {
                await a.ReplyAsync("Некого атаковать в радиусе 30");
                return;
            }
            await a.ReplyAsync($"Атакую {victim.Code}#{victim.Id} (хп {victim.Health:0.#})");
            bot.Movement.FocusEntityId = victim.Id;
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && bot.Entities.Get(victim.Id) is { } v &&
                   !CombatTargets.Defeated(v.Health))
            {
                if (bot.Self.Position is { } p && v.DistanceTo(p.X, p.Y, p.Z) > 2.3)
                {
                    await bot.Movement.PursueAsync(
                        () => bot.Entities.Get(victim.Id) is { } t ? (t.X, t.Z) : null,
                        stopDistance: 1.8, maxSeconds: 8, ct: a.Token);
                    continue;
                }
                await bot.Movement.FaceAsync(v.X, v.Z);
                await bot.Actions.AttackEntityAsync(v.Id);
                await Task.Delay(900);
            }
            bot.Movement.FocusEntityId = null;
            // «ГОТОВ» — ТОЛЬКО ПО НУЛЮ ОТ СЕРВЕРА. Пропал из виду — это не
            // победа, а пропал: тем же правилом, что и в бою
            var vv = bot.Entities.Get(victim.Id);
            await a.ReplyAsync(vv == null
                ? "Цель пропала из виду — победы не считаю"
                : CombatTargets.Defeated(vv.Health)
                    ? "Готов!"
                    : $"Не добил (хп {vv.Health:0.#})");
        });

        bot.Commands.Add("ямтест", "ямтест [ширина] — выкопать ров и попробовать его перепрыгнуть", async a =>
        {
            if (bot.Self.Position is not { } p) return;
            int x0 = (int)Math.Floor(p.X), y0 = (int)Math.Floor(p.Y), z0 = (int)Math.Floor(p.Z);
            int width = 2;
            if (a.Words.Length == 1 && ChatCommandArgs.TryParseCoord(a.Words[0], out double w))
                width = Math.Clamp((int)w, 1, 8);
            int far = 3 + width; // первая клетка твёрдой земли за рвом

            // Тот же отказ и по тому же праву, что у «!стенд»: без творческого
            // режима ров копался бы киркой, а «Выломал N блоков» врало бы про
            // проверенный стенд (см. ModeInsurance.WhyStandCannotRun)
            if (ModeInsurance.WhyStandCannotRun(
                    bot.Self.May(Vintagestory.API.Server.Privilege.gamemode)) is { } нельзя)
            {
                await a.ReplyAsync(нельзя);
                return;
            }

            // Ров заданной ширины поперёк пути, в 3 клетках впереди по +X
            int dug = 0;
            try
            {
                await bot.SayAsync($"/player {bot.Name} gamemode creative");
                await Task.Delay(1500);
                for (int dx = 3; dx < far; dx++)
                for (int dz = -7; dz <= 7; dz++)   // широкий ров: в обход невыгодно
                {
                    // Копаем от поверхности именно этой колонки — рельеф неровный
                    int? ground = bot.World.FindStandableY(x0 + dx, y0, z0 + dz, up: 4, down: 4);
                    if (ground == null) continue;
                    for (int d = 0; d < 3; d++)
                    {
                        var cell = new BlockPos(x0 + dx, ground.Value - 1 - d, z0 + dz);
                        if (bot.World.GetBlockId(cell.X, cell.Y, cell.Z) == 0) continue;
                        await bot.Actions.BreakBlockAsync(cell);
                        dug++;
                        await Task.Delay(60);
                    }
                }
            }
            finally
            {
                // Что бы ни случилось — бот возвращается в выживание
                await Task.Delay(1000);
                await bot.SayAsync($"/player {bot.Name} gamemode survival");
                await Task.Delay(1500);
            }

            // Проверяем, что ров получился: перед ним опора есть, в нём — нет
            int? edge = bot.World.FindSupportY(x0 + 2, y0, z0, up: 3, down: 3);
            bool gap = edge != null;
            for (int dx = 3; dx < far && gap; dx++)
                if (bot.World.FindSupportY(x0 + dx, edge!.Value, z0, up: 1, down: 2) != null)
                    gap = false;
            await a.ReplyAsync($"Выломал {dug} блоков, ров шириной {width} {(gap ? "готов" : "НЕ получился")} " +
                               $"(край {edge}, дно рва {bot.World.FindSupportY(x0 + 3, y0, z0, up: 2, down: 8)})");
            if (!gap)
            {
                // Разрез колонки рва: что там на самом деле
                var column = new List<string>();
                for (int dy = 2; dy >= -6; dy--)
                    column.Add($"{y0 + dy}:{bot.World.GetBlockCode(x0 + 3, y0 + dy, z0) ?? "?"}");
                await a.ReplyAsync("Колонка рва: " + string.Join(", ", column));
                return;
            }

            var opt = bot.Movement.PathOptions.Clone();
            opt.FallDamageBudget = bot.Movement.MaxFallDamage;
            var path = bot.Movement.PathFinder.FindPath(bot.World,
                new BlockPos(x0, y0, z0), new BlockPos(x0 + far + 3, y0, z0), opt);
            int jumps = 0;
            if (path != null)
                for (int i = 1; i < path.Count; i++)
                    if (Math.Abs(path[i].X - path[i - 1].X) + Math.Abs(path[i].Z - path[i - 1].Z) >= 2)
                        jumps++;
            await a.ReplyAsync(path == null
                ? "Маршрут через ров не найден"
                : $"Маршрут: {path.Count} точек, прыжков через ров {jumps}");

            bool ok = await bot.Movement.MoveToSmartAsync(x0 + far + 3.5, z0 + 0.5, 0.5,
                a.Token);
            await a.ReplyAsync(ok
                ? $"Перебрался! ({bot.PlayerCoordsHere()})"
                : $"Не смог ({bot.PlayerCoordsHere()})");
        });

        bot.Commands.Add("лестница", "найти ближайшую лестницу и залезть по ней", async a =>
        {
            if (bot.Self.Position is not { } p) return;
            int cx = (int)Math.Floor(p.X), cy = (int)Math.Floor(p.Y), cz = (int)Math.Floor(p.Z);

            // Ищем лестницы в прогруженной округе (в тоннелях они ниже нас)
            (BlockPos pos, double dist)? found = null;
            for (int dx = -50; dx <= 50; dx++)
            for (int dz = -50; dz <= 50; dz++)
            for (int dy = -45; dy <= 20; dy++)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (!bot.World.IsClimbable(x, y, z)) continue;
                double d = Math.Sqrt(dx * dx + dz * dz + dy * dy);
                if (found == null || d < found.Value.dist) found = (new BlockPos(x, y, z), d);
            }
            if (found is not { } lad)
            {
                await a.ReplyAsync("Лестниц в прогруженной округе не вижу");
                return;
            }

            // Границы лестничного столба
            int bottom = lad.pos.Y, top = lad.pos.Y;
            while (bot.World.IsClimbable(lad.pos.X, bottom - 1, lad.pos.Z)) bottom--;
            while (bot.World.IsClimbable(lad.pos.X, top + 1, lad.pos.Z)) top++;
            await a.ReplyAsync($"Лестница в ({a.Here(lad.pos)}) ({lad.dist:0.#} бл): столб с {bottom} по {top}, высота {top - bottom + 1}");

            // Спускаемся к низу и лезем на самый верх
            await bot.SayAsync($"/tp ={lad.pos.X} ={bottom} ={lad.pos.Z}");
            await Task.Delay(2500);
            await a.ReplyAsync($"Я внизу ({bot.Self.Position?.Y:0.#}), лезу наверх");

            var goal = new BlockPos(lad.pos.X, top, lad.pos.Z);
            var opt = bot.Movement.PathOptions.Clone();
            var path = bot.Movement.PathFinder.FindPath(bot.World,
                new BlockPos(lad.pos.X, bottom, lad.pos.Z), goal, opt);
            await a.ReplyAsync(path == null
                ? "Маршрут наверх не построился"
                : $"Маршрут наверх: {string.Join(" -> ", path.Take(8))}");

            // Цель по высоте — обычный MoveToSmart знает только X/Z
            bool ok = await bot.Movement.MoveToCellAsync(goal, a.Token);
            await a.ReplyAsync($"{(ok ? "Залез" : "Не залез")}: ({bot.PlayerCoordsHere()}), " +
                               $"верх лестницы {top}");

            // И обратно вниз — спуск тоже должен работать
            await Task.Delay(1000);
            bool down = await bot.Movement.MoveToCellAsync(
                new BlockPos(lad.pos.X, bottom, lad.pos.Z), a.Token);
            await a.ReplyAsync($"{(down ? "Спустился" : "Не спустился")}: Y={bot.Self.Position?.Y:0.#} (низ {bottom})");

            // Спуск мог пройти и мимо лестницы (спрыгнуть рядом бывает дешевле).
            // Повторяем с запретом падений: тогда единственный путь вниз — лестница
            await Task.Delay(1000);
            if (!await bot.Movement.MoveToCellAsync(goal, a.Token))
            {
                await a.ReplyAsync("Второй подъём не удался");
                return;
            }
            var opts = bot.Movement.PathOptions;
            int savedDrop = opts.SafeDropDistance, savedFall = opts.MaxDeliberateFall;
            opts.SafeDropDistance = 1;
            opts.MaxDeliberateFall = 0;
            try
            {
                await Task.Delay(500);
                bool byLadder = await bot.Movement.MoveToCellAsync(
                    new BlockPos(lad.pos.X, bottom, lad.pos.Z), a.Token);
                await a.ReplyAsync($"{(byLadder ? "Спустился по лестнице" : "По лестнице спуститься не смог")}: " +
                                   $"Y={bot.Self.Position?.Y:0.#} (низ {bottom})");
            }
            finally
            {
                opts.SafeDropDistance = savedDrop;
                opts.MaxDeliberateFall = savedFall;
            }
        });

        bot.Commands.Add("водатест", "найти водоём и переплыть его", async a =>
        {
            if (bot.Self.Position is not { } wp) return;
            int cx = (int)wp.X, cy = (int)wp.Y, cz = (int)wp.Z;

            (BlockPos pos, double dist)? found = null;
            for (int dx = -60; dx <= 60; dx++)
            for (int dz = -60; dz <= 60; dz++)
            for (int dy = -25; dy <= 10; dy++)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (!bot.World.IsWaterSurface(x, y, z)) continue;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (found == null || d < found.Value.dist) found = (new BlockPos(x, y, z), d);
            }
            if (found is not { } w)
            {
                await a.ReplyAsync("Водоёма рядом не нашёл");
                return;
            }

            BlockPos? Shore(int step)
            {
                for (int i = 1; i <= 60; i++)
                {
                    int x = w.pos.X + step * i;
                    if (bot.World.IsWater(x, w.pos.Y, w.pos.Z)) continue;
                    int? y = bot.World.FindStandableY(x, w.pos.Y, w.pos.Z, up: 3, down: 3);
                    return y is { } yy ? new BlockPos(x, yy, w.pos.Z) : null;
                }
                return null;
            }
            if (Shore(-1) is not { } start || Shore(+1) is not { } goal)
            {
                await a.ReplyAsync($"Вода {w.pos} есть, но берегов вдоль X не нашёл");
                return;
            }

            await a.ReplyAsync($"Водоём: берега ({a.Here(start)}) и ({a.Here(goal)}), ширина {Math.Abs(goal.X - start.X)} бл");
            await bot.SayAsync($"/tp ={start.X} ={start.Y} ={start.Z}");
            await Task.Delay(2500);

            using var watch = new CancellationTokenSource();
            int swimTicks = 0;
            _ = Task.Run(async () =>
            {
                while (!watch.IsCancellationRequested)
                {
                    if (bot.Movement.IsSwimming) swimTicks++;
                    await Task.Delay(400, watch.Token).ContinueWith(_ => { });
                }
            });

            bool ok = await bot.Movement.MoveToSmartAsync(goal.X + 0.5, goal.Z + 0.5, 0.4,
                a.Token);
            watch.Cancel();
            await a.ReplyAsync(ok
                ? $"Переплыл! ({bot.PlayerCoordsHere()}), тиков вплавь {swimTicks}"
                : "Не доплыл");
        });
    }
}

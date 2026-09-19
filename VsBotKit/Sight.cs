// Луч зрения бота. Геометрия — целиком игровая: боксы блоков берутся у самих
// объектов Block (GetSelectionBoxes / GetCollisionBoxes), а пересечение луча
// с боксом считает класс игры AABBIntersectionTest — тот же, которым клиент
// наводит прицел. Своего здесь только обход клеток вдоль луча.

using GlobalConstants = Vintagestory.API.Config.GlobalConstants;

// Низкоуровневая часть живёт в BotGamePhysics по той же причине, что и мост
// к физике: у библиотеки свой BlockPos, и в её пространстве имён игровые
// типы читались бы неоднозначно. Здесь все имена — ИГРОВЫЕ.
namespace BotGamePhysics
{
    using Vintagestory.API.Common;
    using Vintagestory.API.MathTools;
    using WorldModel = VsBotKit.WorldModel;

    /// <summary>
    /// Куда луч попал: клетка, точка внутри неё (0..1 по каждой оси — ровно то,
    /// что настоящий клиент кладёт в HitX/HitY/HitZ), грань и расстояние от глаз.
    /// </summary>
    internal readonly record struct CellHit(
        int X, int Y, int Z,
        double LocalX, double LocalY, double LocalZ,
        int Face, double Distance);

    /// <summary>
    /// Пускает луч по клеткам мира и говорит, во что он упёрся первым.
    ///
    /// Разделение боксов не случайно:
    /// - ПРЕГРАДА по дороге — только коллизия (стена, руда, закрытая дверь).
    ///   Трава, цветы, факел телу не мешают и взгляд не перекрывают;
    /// - ЦЕЛЬ проверяется боксом выделения — это то, по чему щёлкает игрок
    ///   (у травы коллизии нет, а выделение есть, и сломать её игрок может).
    /// Пустая клетка (ставим блок в воздух) — это вся клетка целиком.
    /// </summary>
    internal sealed class BlockRay
    {
        private readonly WorldModel world;
        private readonly BotBlockAccessor blocks;

        public BlockRay(WorldModel world)
        {
            this.world = world;
            // Тот же доступ к блокам, что и у игровой физики: настоящие объекты
            // Block из реестра сервера, с поправкой на двери
            blocks = new BotBlockAccessor(world);
        }

        /// <summary>
        /// Идём по клеткам от глаз в направлении dir, пока не упрёмся в коллизию
        /// или не дойдём до клетки цели. Возвращает первое попадание:
        /// если его клетка не совпала с целью — взгляд перекрыт именно ею.
        /// null — луч ни во что не попал (мимо цели или дальше maxDist).
        /// </summary>
        /// <param name="ignoreStartCell">
        /// Не считать преградой клетку, в которой стоит сам смотрящий.
        ///
        /// Нужно тому, кто пускает луч НЕ ИЗ ГЛАЗ, а от ног (см.
        /// <see cref="VsBotKit.Sight.LookAtCreature"/>): у ног бот часто стоит
        /// в клетке с коллизией — на лестнице, в снегу, в проёме двери, — и
        /// луч упирался бы в неё же на первом шаге. Своя клетка преградой
        /// МЕЖДУ нами быть не может: мы оба в ней не помещаемся.
        /// </param>
        public CellHit? Trace(double ox, double oy, double oz,
            double dx, double dy, double dz, double maxDist,
            int targetX, int targetY, int targetZ,
            bool ignoreStartCell = false)
        {
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9 || double.IsNaN(len))
                return null;
            dx /= len; dy /= len; dz /= len;

            var geom = new AABBIntersectionTest(null!)
            {
                ray = new Ray(new Vec3d(ox, oy, oz), new Vec3d(dx, dy, dz))
            };

            int cx = (int)Math.Floor(ox), cy = (int)Math.Floor(oy), cz = (int)Math.Floor(oz);
            int startX = cx, startY = cy, startZ = cz;
            int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
            double dtX = sx != 0 ? Math.Abs(1.0 / dx) : double.PositiveInfinity;
            double dtY = sy != 0 ? Math.Abs(1.0 / dy) : double.PositiveInfinity;
            double dtZ = sz != 0 ? Math.Abs(1.0 / dz) : double.PositiveInfinity;
            double tX = sx > 0 ? (cx + 1 - ox) / dx : sx < 0 ? (cx - ox) / dx : double.PositiveInfinity;
            double tY = sy > 0 ? (cy + 1 - oy) / dy : sy < 0 ? (cy - oy) / dy : double.PositiveInfinity;
            double tZ = sz > 0 ? (cz + 1 - oz) / dz : sz < 0 ? (cz - oz) / dz : double.PositiveInfinity;

            double t = 0;
            // Дальность руки меньше 5 блоков, так что клеток на пути единицы;
            // ограничение — только страховка от вырожденного направления
            for (int guard = 0; guard < 512; guard++)
            {
                bool isTarget = cx == targetX && cy == targetY && cz == targetZ;
                bool own = ignoreStartCell && !isTarget &&
                           cx == startX && cy == startY && cz == startZ;
                Cuboidf[] boxes = own ? []
                    : isTarget ? TargetBoxes(cx, cy, cz)
                    : CollisionBoxes(cx, cy, cz);
                if (boxes.Length > 0 && FirstBox(geom, boxes, cx, cy, cz, ox, oy, oz) is { } hit)
                    return hit;
                if (isTarget || t > maxDist)
                    return null;

                if (tX < tY && tX < tZ) { cx += sx; t = tX; tX += dtX; }
                else if (tY < tZ) { cy += sy; t = tY; tY += dtY; }
                else if (!double.IsPositiveInfinity(tZ)) { cz += sz; t = tZ; tZ += dtZ; }
                else return null;   // направление вырождено — идти некуда
            }
            return null;
        }

        /// <summary>
        /// Точки, куда имеет смысл целиться в этой клетке: центры боксов и
        /// центры их граней. Игрок ведь тоже наводит курсор на ВИДИМУЮ часть
        /// блока, а не строго в его середину. preferFace (0..5, как
        /// BlockFacing.Index) ставится первым — по нему кликает вызывающий.
        /// </summary>
        public List<(double X, double Y, double Z)> AimPoints(int x, int y, int z, int preferFace)
        {
            var result = new List<(double, double, double)>(8);
            foreach (var b in TargetBoxes(x, y, z))
            {
                if (b == null)
                    continue;
                double mx = (b.X1 + b.X2) / 2.0, my = (b.Y1 + b.Y2) / 2.0, mz = (b.Z1 + b.Z2) / 2.0;

                (double X, double Y, double Z) FaceCenter(int face) => face switch
                {
                    0 => (mx, my, b.Z1),   // север (-Z)
                    1 => (b.X2, my, mz),   // восток (+X)
                    2 => (mx, my, b.Z2),   // юг (+Z)
                    3 => (b.X1, my, mz),   // запад (-X)
                    4 => (mx, b.Y2, mz),   // верх (+Y)
                    _ => (mx, b.Y1, mz),   // низ (-Y)
                };

                void Add((double X, double Y, double Z) p) =>
                    result.Add((x + p.X, y + p.Y, z + p.Z));

                if (preferFace is >= 0 and < 6)
                    Add(FaceCenter(preferFace));
                Add((mx, my, mz));
                for (int f = 0; f < 6; f++)
                    if (f != preferFace)
                        Add(FaceCenter(f));
            }
            return result;
        }

        /// <summary>Код блока в клетке — чтобы честно сказать, чем перекрыто.</summary>
        public string? CodeAt(int x, int y, int z) =>
            world.GetBlockCode(new VsBotKit.BlockPos(x, y, z));

        // ---------------- боксы ----------------

        /// <summary>Бокс цели: по нему щёлкает игрок. Пустая клетка — вся клетка.</summary>
        private Cuboidf[] TargetBoxes(int x, int y, int z)
        {
            var boxes = Boxes(x, y, z, selection: true);
            if (boxes.Length > 0)
                return boxes;
            boxes = Boxes(x, y, z, selection: false);
            return boxes.Length > 0 ? boxes : [FullCell];
        }

        private static readonly Cuboidf FullCell = new(0, 0, 0, 1, 1, 1);

        /// <summary>Преграда: только настоящая коллизия блока.</summary>
        private Cuboidf[] CollisionBoxes(int x, int y, int z) => Boxes(x, y, z, selection: false);

        private Cuboidf[] Boxes(int x, int y, int z, bool selection)
        {
            var pos = new BlockPos(x, y, z, 0);
            Block block;
            try { block = blocks.GetBlock(pos); }
            catch { return []; }
            if (block == null || block.BlockId == 0)
                return [];

            try
            {
                var boxes = selection
                    ? block.GetSelectionBoxes(blocks, pos)
                    : block.GetCollisionBoxes(blocks, pos);
                if (boxes is { Length: > 0 })
                    return boxes;
            }
            catch
            {
                // Модовый или необычный блок может требовать блок-сущность,
                // которой у бота нет: тогда берём боксы как есть из описания
            }

            var raw = selection ? block.SelectionBoxes : block.CollisionBoxes;
            if (raw is { Length: > 0 })
                return raw;

            // Последний рубеж — то, что модель мира уже посчитала по реестру
            float top = world.GetCollisionTop(x, y, z);
            return top > 0 ? [new Cuboidf(0, 0, 0, 1, top, 1)] : [];
        }

        /// <summary>
        /// Ближайшее к глазам пересечение луча с боксами клетки. Саму проверку
        /// «луч × параллелепипед» делает код игры (AABBIntersectionTest), чтобы
        /// точка попадания и грань считались ровно как у клиента.
        /// </summary>
        private static CellHit? FirstBox(AABBIntersectionTest geom, Cuboidf[] boxes,
            int x, int y, int z, double ox, double oy, double oz)
        {
            var cuboid = new Cuboidd();
            var hitPos = new Vec3d();
            var face = BlockFacing.DOWN;
            bool found = false;
            double best = double.MaxValue, bx = 0, by = 0, bz = 0;
            int bestFace = 4;

            foreach (var b in boxes)
            {
                if (b == null)
                    continue;
                cuboid.Set(b).Translate(x, y, z);
                if (!geom.RayIntersectsWithCuboid(cuboid, ref face, ref hitPos))
                    continue;
                double dx = hitPos.X - ox, dy = hitPos.Y - oy, dz = hitPos.Z - oz;
                double d = dx * dx + dy * dy + dz * dz;
                if (d >= best)
                    continue;
                best = d;
                bx = hitPos.X; by = hitPos.Y; bz = hitPos.Z;
                bestFace = face.Index;
                found = true;
            }

            if (!found)
                return null;
            // Точку не подгоняем под 0..1: клиент шлёт ровно то, что дал бокс
            return new CellHit(x, y, z, bx - x, by - y, bz - z, bestFace, Math.Sqrt(best));
        }
    }
}

namespace VsBotKit
{
    /// <summary>
    /// Что видно из глаз бота в сторону клетки.
    /// </summary>
    public sealed record SightResult
    {
        /// <summary>Между глазами и целью нет непроходимых блоков.</summary>
        public required bool Visible { get; init; }

        /// <summary>Клетка, в которую смотрели.</summary>
        public required BlockPos Target { get; init; }

        /// <summary>Чем перекрыто (null — не перекрыто либо проверять было нечем).</summary>
        public BlockPos? BlockedBy { get; init; }

        /// <summary>Код перекрывшего блока — для честного сообщения о причине.</summary>
        public string? BlockedByCode { get; init; }

        /// <summary>
        /// Точка попадания ВНУТРИ клетки цели (обычно 0..1 по каждой оси) —
        /// то самое, что настоящий клиент кладёт в HitX/HitY/HitZ.
        /// </summary>
        public double HitX { get; init; } = 0.5;
        public double HitY { get; init; } = 0.5;
        public double HitZ { get; init; } = 0.5;

        /// <summary>Грань, сквозь которую луч вошёл: 0=север,1=восток,2=юг,3=запад,4=верх,5=низ.</summary>
        public int Face { get; init; } = 4;

        /// <summary>
        /// Расстояние от глаз до точки попадания. Сервер меряет ровно его
        /// (ServerSystemBlockSimulation: eyePos.DistanceSq(pos + HitPosition)),
        /// а не расстояние до центра клетки.
        /// </summary>
        public double Distance { get; init; } = double.MaxValue;

        /// <summary>Видно, но дальше вытянутой руки.</summary>
        public bool TooFar { get; init; }

        /// <summary>
        /// false — проверить было нечем: нет позиции бота или реестр блоков
        /// сервера ещё не пришёл. Тогда врать «видно/не видно» нельзя, и
        /// вызывающий возвращается к прежнему поведению.
        /// </summary>
        public bool Known { get; init; } = true;

        /// <summary>Человеческая причина — для журнала бота.</summary>
        public string Reason =>
            !Known ? "проверить нечем"
            : TooFar ? $"далеко: {Distance:0.#} бл"
            : Visible ? "видно"
            : BlockedBy is { } b ? $"мешает {BlockedByCode ?? "блок"} в {b}"
            : "цель не видна";
    }

    /// <summary>
    /// Прямая видимость: честный луч из глаз бота.
    ///
    /// Зачем: сервер луч игрока НЕ повторяет — он проверяет только расстояние
    /// от глаз до точки клика (ServerSystemBlockSimulation.HandleHandInteraction).
    /// Поэтому «сломать блок сквозь стену» или выбрать руду в глубине жилы
    /// технически проходит, но живой игрок так не может. Луч считаем сами.
    ///
    /// Что перекрывает взгляд — только коллизия блока. Трава и цветы взгляд
    /// не перекрывают, стена, руда и закрытая дверь — перекрывают.
    /// </summary>
    public sealed class Sight
    {
        private readonly BotContext ctx;
        private readonly BotGamePhysics.BlockRay ray;

        public Sight(BotContext ctx)
        {
            this.ctx = ctx;
            ray = new BotGamePhysics.BlockRay(ctx.World);
        }

        /// <summary>Дальность руки — из констант игры, как и у Hands.</summary>
        public double Range { get; set; } = GlobalConstants.DefaultPickingRange;

        /// <summary>
        /// Глаза бота: позиция тела плюс LocalEyePos его сущности. Ровно эту
        /// точку сервер берёт для проверки дальности.
        /// </summary>
        public (double X, double Y, double Z)? EyePos
        {
            get
            {
                if (ctx.Self.Position is not { } p)
                    return null;
                // ВНИМАНИЕ: у нашего BotEntity LocalEyePos остаётся НУЛЕВЫМ —
                // он намеренно не вызывает Entity.Initialize (тот тянет рендер
                // и аниматора). Ноль означает «смотрю из ступней»: луч тут же
                // упирается в пол, на котором бот стоит, и бот честно
                // докладывает, что не видит блок у себя под ногами.
                // Поймано живьём: «мешает rock-andesite» — это был его же пол.
                var eye = ctx.Body?.Physics?.Entity.LocalEyePos;
                double eyeY = eye?.Y ?? 0;
                if (eyeY <= 0.1)
                    eyeY = 1.7;               // высота глаз игрока из player.json
                return (p.X + (eye?.X ?? 0), p.Y + eyeY, p.Z + (eye?.Z ?? 0));
            }
        }

        /// <summary>Есть ли чем считать луч: известна позиция и разобран реестр блоков.</summary>
        public bool Ready => EyePos != null && ctx.World.GameBlocksReady;

        /// <summary>Видна ли клетка целиком честно (без учёта дальности руки).</summary>
        public bool CanSee(BlockPos target) => LookAt(target).Visible;

        /// <summary>
        /// Посмотреть на клетку. Перебираются точки прицеливания на боксе цели
        /// (сначала грань <paramref name="preferFace"/>, потом центр и остальные
        /// грани) — так же, как игрок ведёт курсор по видимой части блока.
        /// Берётся первая точка, до которой луч дошёл, не встретив коллизии.
        /// </summary>
        /// <param name="preferFace">
        /// Желаемая грань клика: 0=север,1=восток,2=юг,3=запад,4=верх,5=низ;
        /// -1 — всё равно.
        /// </param>
        public SightResult LookAt(BlockPos target, int preferFace = -1)
        {
            if (EyePos is not { } eye || !ctx.World.GameBlocksReady)
                return new SightResult
                {
                    Visible = true,
                    Target = target,
                    Known = false,
                    Face = preferFace is >= 0 and < 6 ? preferFace : 4,
                };

            // Идти дальше руки смысла нет, но небольшой запас нужен: сервер
            // меряет до точки попадания, а она ближе центра клетки
            double maxDist = Range + 2;

            // Далёкую цель не трассируем вовсе: до неё всё равно не дотянуться,
            // а «не видно» про неё было бы неправдой — мы просто не смотрели
            double toCenter = Math.Sqrt(
                Sq(target.X + 0.5 - eye.X) + Sq(target.Y + 0.5 - eye.Y) + Sq(target.Z + 0.5 - eye.Z));
            if (toCenter > maxDist)
                return new SightResult
                {
                    Visible = false,
                    Target = target,
                    TooFar = true,
                    Distance = toCenter,
                    Face = preferFace is >= 0 and < 6 ? preferFace : 4,
                };

            SightResult? tooFar = null;
            BlockPos? blockedBy = null;
            string? blockedCode = null;

            foreach (var aim in ray.AimPoints(target.X, target.Y, target.Z, preferFace))
            {
                var hit = ray.Trace(eye.X, eye.Y, eye.Z,
                    aim.X - eye.X, aim.Y - eye.Y, aim.Z - eye.Z,
                    maxDist, target.X, target.Y, target.Z);
                if (hit is not { } h)
                    continue;   // мимо: пробуем следующую точку прицеливания

                if (h.X != target.X || h.Y != target.Y || h.Z != target.Z)
                {
                    // Запоминаем первую помеху — о ней и расскажем, если
                    // ни одна точка прицеливания не сработает
                    if (blockedBy == null)
                    {
                        blockedBy = new BlockPos(h.X, h.Y, h.Z);
                        blockedCode = ray.CodeAt(h.X, h.Y, h.Z);
                    }
                    continue;
                }

                var seen = new SightResult
                {
                    Visible = true,
                    Target = target,
                    HitX = h.LocalX,
                    HitY = h.LocalY,
                    HitZ = h.LocalZ,
                    Face = h.Face,
                    Distance = h.Distance,
                    TooFar = h.Distance > Range,
                };
                if (!seen.TooFar)
                    return seen;
                // Видно, но не дотянуться: вдруг другая грань окажется ближе
                if (tooFar == null || h.Distance < tooFar.Distance)
                    tooFar = seen;
            }

            if (tooFar != null)
                return tooFar;

            return new SightResult
            {
                Visible = false,
                Target = target,
                BlockedBy = blockedBy,
                BlockedByCode = blockedCode,
                Face = preferFace is >= 0 and < 6 ? preferFace : 4,
            };
        }

        /// <summary>
        /// ЧТО МЕЖДУ МНОЙ И ЖИВЫМ: перекрыт ли путь от моего тела к его.
        ///
        /// СЧИТАЕТСЯ ТЕМ ЖЕ СПОСОБОМ, ЧТО И В САМОЙ ИГРЕ, а не по-своему.
        /// Тварь бьёт только по прямой видимости: и ближний бой, и стрельба
        /// проходят через AiTaskBaseTargetable.hasDirectContact (VSEssentials
        /// 1.22.6), а он пускает World.RayTraceForSelection ТРИЖДЫ — от ног
        /// (+1/32 бл), от 7/16 роста и от 14/16 роста, поднимая обе точки
        /// разом, — и довольствуется ОДНИМ чистым лучом. Повторяем ровно это:
        /// перекрыто — только если перекрыты все три.
        ///
        /// ОБЕ СТОРОНЫ МЕРЯЮТСЯ СВОИМ РОСТОМ — И ЭТО ДОЛГ, А НЕ ЗАМЫСЕЛ.
        ///
        /// Здесь стояло «рост твари сервер не шлёт», и это НЕПРАВДА: 26.08
        /// разобрано, что <c>Packet_EntityType</c> везёт <c>CollisionBox*</c> и
        /// <c>SelectionBox*</c>, реестр их принимает и отдаёт
        /// (<see cref="EntityTypes.SightBox"/>), и лук уже целится по НАСТОЯЩЕМУ
        /// росту цели. Игра на стороне ЦЕЛИ берёт её собственный
        /// <c>SelectionBox.Y2</c>; здесь по-прежнему берётся свой.
        ///
        /// ПОЧЕМУ ЭТО ПОКА НЕ ПЕРЕДЕЛАНО. Метод спрашивают по КООРДИНАТАМ, кода
        /// цели он не знает, а зовут его трое — лук, охота и стражник. Менять
        /// луч сразу всем без живого стенда — значит чинить вслепую то, что
        /// сейчас работает.
        ///
        /// РАСХОЖДЕНИЕ БЕЗОПАСНОЕ, И ВОТ ПОЧЕМУ. Лучей три, и НИЖНИЙ идёт по
        /// ступням (1/32 бл) — ниже любой точки прицеливания при любом росте
        /// цели; довольствуемся мы одним чистым лучом. Значит ошибиться можно
        /// только в сторону «преграды не вижу», а это правильная сторона:
        /// придумывать стену, которой не видел, нельзя.
        ///
        /// Луч идёт от НОГ, а не от глаз, — как у игры, — поэтому своя клетка
        /// из счёта выброшена: бот часто стоит в клетке с коллизией (лестница,
        /// снег, проём двери), и она преградой МЕЖДУ нами быть не может.
        /// </summary>
        /// <returns>
        /// Visible=true — хотя бы один луч прошёл. BlockedBy — клетка, в
        /// которую упёрся первый (самый низкий) луч: о ней и говорим человеку.
        /// Known=false — считать было нечем (позиция неизвестна или реестр
        /// блоков сервера ещё не пришёл); врать про преграду в этом случае
        /// нельзя, и зовущий обязан считать, что преграды нет.
        /// </returns>
        public SightResult LookAtCreature(double x, double y, double z)
        {
            var target = new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
            if (ctx.Self.Position is not { } me || !ctx.World.GameBlocksReady)
                return new SightResult { Visible = true, Target = target, Known = false };

            // Свой рост — настоящий, из коробки тела на игровой физике; а пока
            // тела нет — тот же ответ, что и у неё самой (<see cref="BodySize"/>),
            // а не своя копия числа: две правды о собственном росте разошлись бы
            // молча, и увидеть это можно было бы только по промахам взгляда
            double height = ctx.Body?.Physics?.Entity.SelectionBox is { Y2: > 0 } box
                ? box.Y2
                : BodySize.Height;
            double maxDist = Math.Sqrt(Sq(x - me.X) + Sq(y - me.Y) + Sq(z - me.Z)) + 2;

            BlockPos? blockedBy = null;
            string? blockedCode = null;
            // Те же три высоты, что у игры: подъём ОДИНАКОВЫЙ с обеих сторон
            foreach (double up in new[] { 1 / 32.0, height * 7 / 16.0, height * 14 / 16.0 })
            {
                double fromY = me.Y + up, toY = y + up;
                int tx = (int)Math.Floor(x), ty = (int)Math.Floor(toY), tz = (int)Math.Floor(z);
                var hit = ray.Trace(me.X, fromY, me.Z,
                    x - me.X, toY - fromY, z - me.Z, maxDist, tx, ty, tz, ignoreStartCell: true);
                if (hit is not { } h || (h.X == tx && h.Y == ty && h.Z == tz))
                    // Дошли до его клетки (или ушли мимо, ни во что не упёршись) —
                    // этого луча хватает: игра засчитывает контакт по первому же
                    return new SightResult { Visible = true, Target = target };
                blockedBy ??= new BlockPos(h.X, h.Y, h.Z);
                blockedCode ??= ray.CodeAt(h.X, h.Y, h.Z);
            }

            return new SightResult
            {
                Visible = false,
                Target = target,
                BlockedBy = blockedBy,
                BlockedByCode = blockedCode
            };
        }

        private static double Sq(double v) => v * v;
    }
}

namespace VsBotKit;

/// <summary>Каким ходом бот попадает в клетку маршрута.</summary>
public enum StepKind
{
    /// <summary>Обычный шаг (в том числе на ступеньку вверх и на клетку вниз).</summary>
    Walk,
    /// <summary>Прыжок через провал.</summary>
    Jump,
    /// <summary>Прыжок на уступ ВЫШЕ себя.</summary>
    JumpUp,
    /// <summary>Прыжок с зацепом за лестницу в полёте.</summary>
    JumpToLadder,
    /// <summary>Лазание по лестнице вверх или вниз.</summary>
    Climb,
    /// <summary>Осознанное спрыгивание вниз (за него платят здоровьем).</summary>
    Drop,
    /// <summary>Плыть по поверхности.</summary>
    Swim,
    /// <summary>Плыть под водой.</summary>
    Dive,
}

/// <summary>
/// Точка маршрута ВМЕСТЕ С ТИПОМ ХОДА.
///
/// Зачем это нужно. Планировщик знает точно, чем именно он попадает в клетку:
/// он сам решал, что здесь прыжок с разбегом, там зацеп за лестницу, а тут
/// спрыгивание за счёт здоровья. Раньше он возвращал голый список клеток и
/// это знание выбрасывал — а исполнитель ПЕРЕУГАДЫВАЛ тип хода по геометрии.
///
/// Каждое расхождение между «что задумал планировщик» и «что угадало тело»
/// стоило нам живого бага: тело шло шагом там, где планировался прыжок с
/// разбега, и не долетало; не цеплялось за лестницу, потому что не знало,
/// что это зацеп; шагало с обрыва, считая спрыгивание обычным шагом.
///
/// Это же и главное условие сменности поиска пути: чужая реализация может
/// придумывать свои ходы, и тело обязано узнать о них от неё, а не гадать.
/// </summary>
/// <param name="Pos">Клетка, куда встают ноги.</param>
/// <param name="Kind">Чем в неё попадают.</param>
public readonly record struct PathStep(BlockPos Pos, StepKind Kind = StepKind.Walk)
{
    /// <summary>Шаг сам по себе — это прежде всего клетка, поэтому подставляется вместо неё.</summary>
    public static implicit operator BlockPos(PathStep step) => step.Pos;

    public int X => Pos.X;
    public int Y => Pos.Y;
    public int Z => Pos.Z;

    /// <summary>Ход, требующий отрыва от земли: разбег и удержание кнопки.</summary>
    public bool IsJump => Kind is StepKind.Jump or StepKind.JumpUp or StepKind.JumpToLadder;

    public override string ToString() => Kind == StepKind.Walk ? Pos.ToString() : $"{Pos} [{Kind}]";
}

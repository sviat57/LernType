using System.Text;
using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Courses;

/// <summary>Validates the complete structural and content contract of the bundled course catalog.</summary>
public static class CourseCatalogValidator
{
    private const int SupportedRevision = 1;
    private const int RequiredPublishedLessonCount = 24;
    private static readonly CourseStepKind[] RequiredStepSequence =
    [
        CourseStepKind.Briefing,
        CourseStepKind.Writing,
        CourseStepKind.Reading,
        CourseStepKind.ListeningSpeaking,
        CourseStepKind.Rule,
        CourseStepKind.Checkpoint
    ];

    private static readonly IReadOnlyDictionary<GermanLevel, int> RequiredExamQuestionCounts =
        new Dictionary<GermanLevel, int>
        {
            [GermanLevel.A0] = 12,
            [GermanLevel.A1] = 16,
            [GermanLevel.A2] = 20
        };

    /// <summary>
    /// Validates <paramref name="catalog"/> and throws <see cref="InvalidDataException"/> at the
    /// first field that would make course navigation, rendering or progress identity ambiguous.
    /// </summary>
    public static void Validate(CourseCatalog? catalog)
    {
        if (catalog is null)
        {
            throw Invalid("catalog", "Каталог отсутствует.");
        }
        if (catalog.Revision != SupportedRevision)
        {
            throw Invalid("revision", $"Поддерживается только ревизия {SupportedRevision}.");
        }
        if (catalog.Track is null)
        {
            throw Invalid("track", "Учебный путь отсутствует.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        ValidateId(catalog.Track.Id, "track.id", ids);
        ValidateText(catalog.Track.Title, "track.title");
        RequireCollection(catalog.Track.Courses, "track.courses");

        var expectedLevels = Enum.GetValues<GermanLevel>();
        if (catalog.Track.Courses.Count != expectedLevels.Length)
        {
            throw Invalid("track.courses", $"Ожидается {expectedLevels.Length} курсов A0–C2.");
        }

        ValidateConsecutiveOrder(catalog.Track.Courses.Select(course => course.Order), "track.courses");
        var publishedLessonCount = 0;
        for (var index = 0; index < catalog.Track.Courses.Count; index++)
        {
            var course = catalog.Track.Courses[index]
                ?? throw Invalid($"track.courses[{index}]", "Курс отсутствует.");
            var expectedLevel = expectedLevels[index];
            if (course.Level != expectedLevel)
            {
                throw Invalid($"track.courses[{index}].level", $"Ожидается уровень {expectedLevel}.");
            }
            publishedLessonCount += ValidateCourse(course, index, ids);
        }

        if (publishedLessonCount != RequiredPublishedLessonCount)
        {
            throw Invalid(
                "track.courses",
                $"В опубликованных курсах должно быть ровно {RequiredPublishedLessonCount} урока, найдено {publishedLessonCount}.");
        }
    }

    private static int ValidateCourse(CourseDefinition course, int courseIndex, HashSet<string> ids)
    {
        var path = $"track.courses[{courseIndex}]";
        ValidateId(course.Id, $"{path}.id", ids);
        ValidateText(course.Title, $"{path}.title");
        ValidateText(course.Subtitle, $"{path}.subtitle");
        ValidateText(course.Outcome, $"{path}.outcome");
        EnsureDefined(course.Availability, $"{path}.availability");
        EnsureDefined(course.Level, $"{path}.level");
        RequireCollection(course.Units, $"{path}.units");

        var shouldBePublished = course.Level is GermanLevel.A0 or GermanLevel.A1 or GermanLevel.A2;
        var expectedAvailability = shouldBePublished ? CourseAvailability.Published : CourseAvailability.Planned;
        if (course.Availability != expectedAvailability)
        {
            throw Invalid($"{path}.availability", $"Для {course.Level} ожидается {expectedAvailability}.");
        }

        if (!shouldBePublished)
        {
            if (course.Units.Count != 0)
            {
                throw Invalid($"{path}.units", "Запланированный курс не должен содержать запускаемые разделы.");
            }
            if (course.Exam is not null)
            {
                throw Invalid($"{path}.exam", "Запланированный курс не должен содержать запускаемый экзамен.");
            }
            return 0;
        }

        if (course.Units.Count == 0)
        {
            throw Invalid($"{path}.units", "Опубликованный курс должен содержать разделы.");
        }
        if (course.Exam is null)
        {
            throw Invalid($"{path}.exam", "Опубликованный курс должен содержать итоговый экзамен.");
        }

        ValidateConsecutiveOrder(course.Units.Select(unit => unit.Order), $"{path}.units");
        var lessonCount = 0;
        for (var index = 0; index < course.Units.Count; index++)
        {
            var unit = course.Units[index]
                ?? throw Invalid($"{path}.units[{index}]", "Раздел отсутствует.");
            lessonCount += ValidateUnit(unit, $"{path}.units[{index}]", ids);
        }
        ValidateExam(course.Exam, course.Level, $"{path}.exam", ids);
        return lessonCount;
    }

    private static int ValidateUnit(CourseUnitDefinition unit, string path, HashSet<string> ids)
    {
        ValidateId(unit.Id, $"{path}.id", ids);
        ValidateText(unit.Title, $"{path}.title");
        ValidateText(unit.Outcome, $"{path}.outcome");
        RequireCollection(unit.Lessons, $"{path}.lessons");
        if (unit.Lessons.Count == 0)
        {
            throw Invalid($"{path}.lessons", "Раздел должен содержать хотя бы один урок.");
        }

        ValidateConsecutiveOrder(unit.Lessons.Select(lesson => lesson.Order), $"{path}.lessons");
        for (var index = 0; index < unit.Lessons.Count; index++)
        {
            var lesson = unit.Lessons[index]
                ?? throw Invalid($"{path}.lessons[{index}]", "Урок отсутствует.");
            ValidateLesson(lesson, $"{path}.lessons[{index}]", ids);
        }
        return unit.Lessons.Count;
    }

    private static void ValidateLesson(CourseLessonDefinition lesson, string path, HashSet<string> ids)
    {
        ValidateId(lesson.Id, $"{path}.id", ids);
        ValidateText(lesson.Title, $"{path}.title");
        ValidateText(lesson.Outcome, $"{path}.outcome");
        if (lesson.EstimatedMinutes is < 1 or > 180)
        {
            throw Invalid($"{path}.estimatedMinutes", "Длительность урока должна быть от 1 до 180 минут.");
        }
        RequireCollection(lesson.Steps, $"{path}.steps");
        if (lesson.Steps.Count != RequiredStepSequence.Length)
        {
            throw Invalid($"{path}.steps", "Каждый урок должен содержать ровно шесть шагов.");
        }
        ValidateConsecutiveOrder(lesson.Steps.Select(step => step.Order), $"{path}.steps");

        for (var index = 0; index < lesson.Steps.Count; index++)
        {
            var step = lesson.Steps[index]
                ?? throw Invalid($"{path}.steps[{index}]", "Шаг отсутствует.");
            var stepPath = $"{path}.steps[{index}]";
            if (step.Kind != RequiredStepSequence[index])
            {
                throw Invalid($"{stepPath}.kind", $"Ожидается {RequiredStepSequence[index]}.");
            }
            ValidateStep(step, stepPath, ids);
        }
    }

    private static void ValidateStep(CourseStepDefinition step, string path, HashSet<string> ids)
    {
        ValidateId(step.Id, $"{path}.id", ids);
        EnsureDefined(step.Kind, $"{path}.kind");
        ValidateText(step.Title, $"{path}.title");
        ValidateText(step.Instruction, $"{path}.instruction");
        ValidateOptionalText(step.RussianText, $"{path}.russianText");
        ValidateOptionalText(step.GermanText, $"{path}.germanText");
        ValidateOptionalText(step.Hint, $"{path}.hint");

        if (step.Table is not null)
        {
            ValidateTable(step.Table, $"{path}.table");
        }

        var isExplanation = step.Kind is CourseStepKind.Briefing or CourseStepKind.Rule;
        if (isExplanation && step.Task is not null)
        {
            throw Invalid($"{path}.task", "Шаг объяснения не должен содержать проверяемое задание.");
        }
        if (!isExplanation && step.Task is null)
        {
            throw Invalid($"{path}.task", "Практический шаг должен содержать проверяемое задание.");
        }
        if (step.Task is not null)
        {
            ValidateTask(step.Task, $"{path}.task", ids);
        }

        if (isExplanation && step.RussianText is null && step.GermanText is null && step.Table is null)
        {
            throw Invalid(path, "Шаг объяснения должен содержать текст или таблицу.");
        }
    }

    private static void ValidateExam(
        CourseExamDefinition exam,
        GermanLevel level,
        string path,
        HashSet<string> ids)
    {
        ValidateId(exam.Id, $"{path}.id", ids);
        ValidateText(exam.Title, $"{path}.title");
        if (exam.PassPercent != 75)
        {
            throw Invalid($"{path}.passPercent", "Порог встроенного экзамена должен составлять 75%.");
        }
        RequireCollection(exam.Questions, $"{path}.questions");
        var requiredCount = RequiredExamQuestionCounts[level];
        if (exam.Questions.Count != requiredCount)
        {
            throw Invalid($"{path}.questions", $"Для {level} требуется ровно {requiredCount} вопросов.");
        }
        ValidateConsecutiveOrder(exam.Questions.Select(question => question.Order), $"{path}.questions");

        var speechCount = 0;
        for (var index = 0; index < exam.Questions.Count; index++)
        {
            var question = exam.Questions[index]
                ?? throw Invalid($"{path}.questions[{index}]", "Вопрос отсутствует.");
            var questionPath = $"{path}.questions[{index}]";
            ValidateId(question.Id, $"{questionPath}.id", ids);
            ValidateText(question.Title, $"{questionPath}.title");
            ValidateTaskFields(
                question.Kind,
                question.Prompt,
                question.Answer,
                question.AcceptedAnswers,
                question.Options,
                question.ModelAnswer,
                question.Skill,
                question.ExerciseType,
                questionPath);
            ValidateOptionalText(question.AudioText, $"{questionPath}.audioText");
            if (question.Skill == LanguageSkill.Listening && question.AudioText is null)
            {
                throw Invalid($"{questionPath}.audioText", "Для задания на аудирование требуется отдельный немецкий аудиостимул.");
            }
            if (question.Skill != LanguageSkill.Listening && question.AudioText is not null)
            {
                throw Invalid($"{questionPath}.audioText", "Аудиостимул допускается только в задании на аудирование.");
            }
            if (question.Kind == CourseTaskKind.SelfRecordedSpeech)
            {
                speechCount++;
            }
        }
        if (speechCount != 2)
        {
            throw Invalid($"{path}.questions", "Экзамен должен содержать ровно два устных задания.");
        }
    }

    private static void ValidateTask(CourseTaskDefinition task, string path, HashSet<string> ids)
    {
        ValidateId(task.Id, $"{path}.id", ids);
        ValidateTaskFields(
            task.Kind,
            task.Prompt,
            task.Answer,
            task.AcceptedAnswers,
            task.Options,
            task.ModelAnswer,
            task.Skill,
            task.ExerciseType,
            path);
    }

    private static void ValidateTaskFields(
        CourseTaskKind kind,
        string prompt,
        string? answer,
        IReadOnlyList<string> acceptedAnswers,
        IReadOnlyList<string> options,
        string? modelAnswer,
        LanguageSkill skill,
        ExerciseType exerciseType,
        string path)
    {
        EnsureDefined(kind, $"{path}.kind");
        EnsureDefined(skill, $"{path}.skill");
        EnsureDefined(exerciseType, $"{path}.exerciseType");
        ValidateText(prompt, $"{path}.prompt");
        ValidateOptionalText(answer, $"{path}.answer");
        ValidateOptionalText(modelAnswer, $"{path}.modelAnswer");
        ValidateTextCollection(acceptedAnswers, $"{path}.acceptedAnswers");
        ValidateTextCollection(options, $"{path}.options");
        EnsureUnique(acceptedAnswers, $"{path}.acceptedAnswers");
        EnsureUnique(options, $"{path}.options");

        switch (kind)
        {
            case CourseTaskKind.ShortAnswer:
            case CourseTaskKind.GapFill:
                RequireAnswer(answer, path);
                RequireEmpty(options, $"{path}.options", "Текстовое задание не должно содержать варианты выбора.");
                if (answer is not null && acceptedAnswers.Contains(answer, StringComparer.Ordinal))
                {
                    throw Invalid($"{path}.acceptedAnswers", "Канонический ответ не нужно дублировать среди вариантов.");
                }
                break;
            case CourseTaskKind.SingleChoice:
                RequireAnswer(answer, path);
                if (options.Count < 2)
                {
                    throw Invalid($"{path}.options", "Задание с выбором должно содержать минимум два варианта.");
                }
                if (answer is not null && !options.Contains(answer, StringComparer.Ordinal))
                {
                    throw Invalid($"{path}.answer", "Канонический ответ должен присутствовать среди вариантов.");
                }
                RequireEmpty(acceptedAnswers, $"{path}.acceptedAnswers", "Задание с выбором не использует свободные варианты ответа.");
                break;
            case CourseTaskKind.SelfRecordedSpeech:
                if (skill != LanguageSkill.Speaking)
                {
                    throw Invalid($"{path}.skill", "Устная самозапись должна проверять навык Speaking.");
                }
                if (modelAnswer is null)
                {
                    throw Invalid($"{path}.modelAnswer", "Для устного задания требуется образец ответа.");
                }
                if (answer is not null)
                {
                    throw Invalid($"{path}.answer", "Устная самозапись не использует текстовый эталон.");
                }
                RequireEmpty(acceptedAnswers, $"{path}.acceptedAnswers", "Устная самозапись не использует текстовые варианты.");
                RequireEmpty(options, $"{path}.options", "Устная самозапись не использует варианты выбора.");
                break;
            default:
                throw Invalid($"{path}.kind", "Неизвестный тип задания.");
        }
    }

    private static void ValidateTable(CourseTableDefinition table, string path)
    {
        ValidateTextCollection(table.Headers, $"{path}.headers");
        if (table.Headers.Count == 0)
        {
            throw Invalid($"{path}.headers", "Таблица должна содержать хотя бы один столбец.");
        }
        RequireCollection(table.Rows, $"{path}.rows");
        if (table.Rows.Count == 0)
        {
            throw Invalid($"{path}.rows", "Таблица должна содержать хотя бы одну строку.");
        }
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex]
                ?? throw Invalid($"{path}.rows[{rowIndex}]", "Строка таблицы отсутствует.");
            ValidateTextCollection(row, $"{path}.rows[{rowIndex}]");
            if (row.Count != table.Headers.Count)
            {
                throw Invalid(
                    $"{path}.rows[{rowIndex}]",
                    $"Ожидается {table.Headers.Count} ячеек, найдено {row.Count}.");
            }
        }
    }

    private static void ValidateConsecutiveOrder(IEnumerable<int> orders, string path)
    {
        var index = 1;
        foreach (var order in orders)
        {
            if (order != index)
            {
                throw Invalid(path, $"Порядок должен быть непрерывным с 1; ожидалось {index}, найдено {order}.");
            }
            index++;
        }
    }

    private static void ValidateId(string value, string path, ISet<string> ids)
    {
        ValidateText(value, path);
        if (!ids.Add(value))
        {
            throw Invalid(path, $"Идентификатор '{value}' уже используется в каталоге.");
        }
    }

    private static void ValidateTextCollection(IReadOnlyList<string>? values, string path)
    {
        if (values is null)
        {
            throw Invalid(path, "Коллекция отсутствует.");
        }
        for (var index = 0; index < values.Count; index++)
        {
            ValidateText(values[index], $"{path}[{index}]");
        }
    }

    private static void EnsureUnique(IReadOnlyList<string> values, string path)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw Invalid(path, "Значения должны быть уникальными.");
        }
    }

    private static void ValidateOptionalText(string? value, string path)
    {
        if (value is not null)
        {
            ValidateText(value, path);
        }
    }

    private static void ValidateText(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(path, "Строка должна быть непустой.");
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Invalid(path, "Строка не должна начинаться или заканчиваться пробелом.");
        }
        if (!value.IsNormalized(NormalizationForm.FormC))
        {
            throw Invalid(path, "Строка должна быть нормализована в Unicode NFC.");
        }
    }

    private static void RequireAnswer(string? answer, string path)
    {
        if (answer is null)
        {
            throw Invalid($"{path}.answer", "Для задания требуется канонический ответ.");
        }
    }

    private static void RequireEmpty<T>(IReadOnlyCollection<T> collection, string path, string message)
    {
        if (collection.Count != 0)
        {
            throw Invalid(path, message);
        }
    }

    private static void RequireCollection<T>(IReadOnlyCollection<T>? collection, string path)
    {
        if (collection is null)
        {
            throw Invalid(path, "Коллекция отсутствует.");
        }
    }

    private static void EnsureDefined<TEnum>(TEnum value, string path)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw Invalid(path, $"Неизвестное значение {typeof(TEnum).Name}: {value}.");
        }
    }

    private static InvalidDataException Invalid(string path, string message) =>
        new($"Некорректный каталог курсов ({path}): {message}");
}

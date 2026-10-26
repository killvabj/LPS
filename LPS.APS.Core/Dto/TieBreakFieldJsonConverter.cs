using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LPS.APS.Core.Dto;

/// <summary>
/// <see cref="TieBreakField"/> 的 JSON 读写（B3 v1.2 §3，2026-10-20）。
///
/// 为什么需要它：
/// 1. <c>JsonSerializerOptions</c> 全链**没有** <c>JsonStringEnumConverter</c>
///    （现有枚举一律按**数值**落库，如 <c>"Field":1</c>／<c>"Operator":0</c>），
///    所以强类型化后的 Tie-break 若不加转换器，就会被当成数值读，字符串一律失败。
/// 2. 存量快照里有**三种写法**：<c>"OrderId"</c>（RuleSetVersion 477/478/1240）、
///    <c>"DemandKey"</c>（Fixture）、<c>"DEMANDKEY"</c>（执行器词表）。
///    若只认 <c>DEMANDKEY</c>，三个已发布快照会**当场反序列化失败**。
///
/// 口径：
/// - 读：字符串先按**别名表**归一化（`DEMANDKEY` ／ 存量写法 `OrderId`），
///   别名表之外一律**抛错**（fail-closed，不静默丢弃 Tie-break——丢了排序就不确定）。
///   数值也接受，直接按枚举值转换（与其它枚举保持一致的落库形态）。
/// - 写：一律写**枚举成员名**（即执行器白名单词），不再产出旧别名。
///
/// 旧别名只作**过渡**：存量快照重新发布后即可从别名表移除。
/// 相关：<see cref="PrioritySegment.StableTieBreakFields"/>。
/// </summary>
public sealed class TieBreakFieldJsonConverter : JsonConverter<TieBreakField>
{
    /// <summary>
    /// 旧写法 → 执行器词表的归一化别名表。键为 <c>Trim().ToUpperInvariant()</c> 后的值。
    /// 加新项前先确认语义同一（不同字段不得共用一个词）。
    /// </summary>
    private static readonly Dictionary<string, TieBreakField> LegacyAliases = new(StringComparer.Ordinal)
    {
        ["DEMANDKEY"] = TieBreakField.DEMANDKEY,  // 现行词表
        ["ORDERID"] = TieBreakField.DEMANDKEY,    // 存量快照 477/478/1240 的写法（2号位 投影层原本手工换算的正是这一条）
        ["ORDERNO"] = TieBreakField.DEMANDKEY,    // 同义写法（订单身份），保留以防治理侧歧义
    };

    public override TieBreakField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            var normalized = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (LegacyAliases.TryGetValue(normalized, out var field))
            {
                return field;
            }

            throw new JsonException(
                $"StableTieBreakFields 出现未知值 '{raw}'：只接受执行器词表 "
                + $"{string.Join(" / ", System.Enum.GetNames<TieBreakField>())}"
                + "（存量写法 " + string.Join(" / ", LegacyAliases.Keys) + " 会自动归一化）。"
                + " 新增字段须先由 1号位 在 DemandPriorityExecutor.KnownFields 落地比较分支。");
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            // 与其它枚举的落库形态一致（数值）；越界值直接抛错。
            var value = reader.GetInt32();
            if (!System.Enum.IsDefined(typeof(TieBreakField), value))
            {
                throw new JsonException($"StableTieBreakFields 数值 {value} 不是已定义的 TieBreakField 值。");
            }

            return (TieBreakField)value;
        }

        throw new JsonException(
            $"StableTieBreakFields 的 JSON 形态非法（{reader.TokenType}），期望字符串或数值。");
    }

    public override void Write(Utf8JsonWriter writer, TieBreakField value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

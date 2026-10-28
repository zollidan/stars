//! Сходство нормализованных названий (ASCII после `normalize_team_name`).
//! Формулы `ratio` и `token_set_ratio` — как в rapidfuzz, шкала 0..100.

use std::collections::BTreeSet;

use rapidfuzz::{distance::indel, fuzz};

/// Возрастные, женские и дублирующие маркеры: их несовпадение меняет саму команду.
pub fn markers(name: &str) -> String {
    let markers: BTreeSet<&str> = name
        .split_whitespace()
        .filter_map(|token| match token {
            "women" | "womens" | "ladies" | "zh" | "zhen" | "w" => Some("w"),
            "reserve" | "reserves" | "res" => Some("res"),
            "ii" | "iii" | "2" | "3" => Some(token),
            _ if token.len() <= 3
                && token.starts_with('u')
                && token.len() > 1
                && token[1..].bytes().all(|byte| byte.is_ascii_digit()) =>
            {
                Some(token)
            }
            _ => None,
        })
        .collect();
    markers.into_iter().collect::<Vec<_>>().join(" ")
}

fn ratio(left: &str, right: &str) -> f64 {
    fuzz::ratio(left.bytes(), right.bytes()) * 100.0
}

/// Сравнивает общие и различающиеся слова; подмножество слов — 100.
fn token_set_ratio(left: &str, right: &str) -> f64 {
    let a: BTreeSet<&str> = left.split_whitespace().collect();
    let b: BTreeSet<&str> = right.split_whitespace().collect();
    if a.is_empty() || b.is_empty() {
        return 0.0;
    }
    let join = |tokens: Vec<&str>| tokens.join(" ");
    let sect = join(a.intersection(&b).copied().collect());
    let diff_ab = join(a.difference(&b).copied().collect());
    let diff_ba = join(b.difference(&a).copied().collect());
    if !sect.is_empty() && (diff_ab.is_empty() || diff_ba.is_empty()) {
        return 100.0;
    }
    let norm = |dist: usize, lensum: usize| {
        if lensum == 0 {
            100.0
        } else {
            100.0 - 100.0 * dist as f64 / lensum as f64
        }
    };
    let separator = usize::from(!sect.is_empty());
    let sect_ab_len = sect.len() + separator + diff_ab.len();
    let sect_ba_len = sect.len() + separator + diff_ba.len();
    let diff = norm(
        indel::distance(diff_ab.bytes(), diff_ba.bytes()),
        sect_ab_len + sect_ba_len,
    );
    if sect.is_empty() {
        return diff;
    }
    let sect_ab = norm(separator + diff_ab.len(), sect.len() + sect_ab_len);
    let sect_ba = norm(separator + diff_ba.len(), sect.len() + sect_ba_len);
    diff.max(sect_ab).max(sect_ba)
}

/// Сходство имён с одинаковыми маркерами; точное совпадение — 100.
pub fn name_similarity(left: &str, right: &str) -> f64 {
    if left == right {
        return 100.0;
    }
    ratio(left, right).max(token_set_ratio(left, right))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(left: f64, right: f64) -> bool {
        (left - right).abs() < 1e-4
    }

    #[test]
    fn scores_match_rapidfuzz() {
        assert!(close(ratio("this is a test", "this is a test!"), 96.551724));
        assert_eq!(
            token_set_ratio("fuzzy was a bear", "fuzzy fuzzy was a bear"),
            100.0
        );
        assert!(close(
            token_set_ratio(
                "fuzzy was a bear but not a dog",
                "fuzzy was a bear but not a cat"
            ),
            92.307692
        ));
        assert_eq!(token_set_ratio("", "zenit"), 0.0);
    }

    #[test]
    fn markers_separate_squads() {
        assert_eq!(markers("juventus u19"), "u19");
        assert_eq!(markers("arsenal women"), "w");
        assert_eq!(markers("dynamo 2 res"), "2 res");
        assert_eq!(markers("u2 under"), "u2");
        assert_eq!(name_similarity("zenit", "zenit"), 100.0);
        assert_eq!(name_similarity("zenit", "zenit st petersburg"), 100.0);
    }
}

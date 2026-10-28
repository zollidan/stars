//! Нормализация названий команд.

use any_ascii::any_ascii;

pub fn normalize_team_name(name: &str) -> String {
    let ascii = any_ascii(&name.to_lowercase().replace('ё', "е"));
    let cleaned = ascii
        .chars()
        .map(|ch| {
            if ch.is_ascii_alphanumeric() {
                ch.to_ascii_lowercase()
            } else {
                ' '
            }
        })
        .collect::<String>();
    let words: Vec<&str> = cleaned.split_whitespace().collect();
    let mut normalized = Vec::with_capacity(words.len());
    let mut index = 0;
    while index < words.len() {
        let word = words[index];
        if matches!(word, "u" | "under")
            && words.get(index + 1).is_some_and(|age| {
                (1..=2).contains(&age.len()) && age.bytes().all(|byte| byte.is_ascii_digit())
            })
        {
            normalized.push(format!("u{}", words[index + 1]));
            index += 2;
            continue;
        }
        normalized.push(
            match word {
                "women" | "womens" | "ladies" | "zh" | "zhen" => "w",
                "reserve" | "reserves" => "res",
                _ => word,
            }
            .to_owned(),
        );
        index += 1;
    }
    let fallback = normalized.join(" ");
    let without_noise = normalized
        .iter()
        .filter(|word| {
            !matches!(
                word.as_str(),
                "fc" | "fk" | "cf" | "sc" | "ac" | "afc" | "cd" | "bk" | "club"
            )
        })
        .map(String::as_str)
        .collect::<Vec<_>>()
        .join(" ");
    if without_noise.is_empty() {
        fallback
    } else {
        without_noise
    }
}

#[cfg(test)]
mod tests {
    use super::normalize_team_name as normalize;

    #[test]
    fn normalizes_names() {
        assert_eq!(normalize("Manchester United FC"), "manchester united");
        assert_eq!(normalize("FC Bayern München"), "bayern munchen");
        assert_eq!(normalize("Реал Мадрид"), "real madrid");
        assert_eq!(normalize("ФК Зенит"), "zenit");
        assert_eq!(normalize("Club León"), "leon");
        assert_eq!(normalize("Juventus Under 19"), "juventus u19");
        assert_eq!(normalize("Arsenal Women"), "arsenal w");
        assert_eq!(normalize("FC"), "fc");
    }
}

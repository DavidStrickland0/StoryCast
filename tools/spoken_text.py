from __future__ import annotations

import re


ONES = (
    "zero", "one", "two", "three", "four",
    "five", "six", "seven", "eight", "nine",
)
TEENS = (
    "ten", "eleven", "twelve", "thirteen", "fourteen",
    "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
)
TENS = (
    "", "", "twenty", "thirty", "forty",
    "fifty", "sixty", "seventy", "eighty", "ninety",
)
ORDINALS = {
    "one": "first",
    "two": "second",
    "three": "third",
    "four": "fourth",
    "five": "fifth",
    "six": "sixth",
    "seven": "seventh",
    "eight": "eighth",
    "nine": "ninth",
    "ten": "tenth",
    "eleven": "eleventh",
    "twelve": "twelfth",
    "thirteen": "thirteenth",
    "fourteen": "fourteenth",
    "fifteen": "fifteenth",
    "sixteen": "sixteenth",
    "seventeen": "seventeenth",
    "eighteen": "eighteenth",
    "nineteen": "nineteenth",
    "twenty": "twentieth",
    "thirty": "thirtieth",
    "forty": "fortieth",
    "fifty": "fiftieth",
    "sixty": "sixtieth",
    "seventy": "seventieth",
    "eighty": "eightieth",
    "ninety": "ninetieth",
    "hundred": "hundredth",
    "thousand": "thousandth",
}
MONTHS = (
    "January|February|March|April|May|June|July|August|"
    "September|October|November|December"
)


def integer_to_words(value: int) -> str:
    if value < 0:
        return "minus " + integer_to_words(-value)
    if value < 10:
        return ONES[value]
    if value < 20:
        return TEENS[value - 10]
    if value < 100:
        tens, remainder = divmod(value, 10)
        return TENS[tens] + (
            f"-{ONES[remainder]}" if remainder else ""
        )
    if value < 1000:
        hundreds, remainder = divmod(value, 100)
        return f"{ONES[hundreds]} hundred" + (
            f" {integer_to_words(remainder)}" if remainder else ""
        )
    if value < 1_000_000:
        thousands, remainder = divmod(value, 1000)
        return f"{integer_to_words(thousands)} thousand" + (
            f" {integer_to_words(remainder)}" if remainder else ""
        )
    return str(value)


def ordinal_to_words(value: int) -> str:
    cardinal = integer_to_words(value)
    separator = max(cardinal.rfind(" "), cardinal.rfind("-"))
    prefix = cardinal[:separator + 1]
    final = cardinal[separator + 1:]
    return prefix + ORDINALS.get(final, final + "th")


def year_to_words(value: int) -> str:
    if 1000 <= value <= 1999:
        century, remainder = divmod(value, 100)
        return integer_to_words(century) + (
            f" {integer_to_words(remainder)}"
            if remainder >= 10
            else f" oh {integer_to_words(remainder)}"
            if remainder
            else " hundred"
        )
    if 2000 <= value <= 2009:
        return "two thousand" + (
            f" {integer_to_words(value - 2000)}"
            if value > 2000
            else ""
        )
    if 2010 <= value <= 2099:
        return "twenty " + integer_to_words(value - 2000)
    return integer_to_words(value)


def normalize_spoken_text(text: str) -> str:
    """Expand numeric forms that commonly destabilize speech synthesis."""
    def replace_dollars(match: re.Match[str]) -> str:
        amount = match.group("amount").replace(",", "")
        whole, _, fraction = amount.partition(".")
        dollars = int(whole)
        cents = int(fraction.ljust(2, "0")) if fraction else 0
        spoken = integer_to_words(dollars) + (" dollar" if dollars == 1 else " dollars")
        if cents:
            spoken += " and " + integer_to_words(cents) + (" cent" if cents == 1 else " cents")
        return spoken

    def replace_date(match: re.Match[str]) -> str:
        month = match.group("month")
        day = ordinal_to_words(int(match.group("day")))
        year = year_to_words(int(match.group("year")))
        return f"{month} {day}, {year}"

    def replace_time(match: re.Match[str]) -> str:
        hour = integer_to_words(int(match.group("hour")))
        minute_value = int(match.group("minute"))
        suffix = match.group("suffix")

        if minute_value == 0:
            spoken = f"{hour} o'clock"
        elif minute_value < 10:
            spoken = f"{hour} oh {integer_to_words(minute_value)}"
        else:
            spoken = f"{hour} {integer_to_words(minute_value)}"

        if suffix:
            letters = re.sub(r"[^a-z]", "", suffix.lower())
            spoken += " " + " ".join(letters.upper())

        return spoken

    # Expand the complete monetary amount before generic integers or years.
    result = re.sub(
        r"\$(?P<amount>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?)(?!\d|,\d|\.\d)",
        replace_dollars,
        text,
    )
    result = re.sub(
        r"\b(?P<label>room\s+(?:number\s+)?#?)(?P<number>\d+)(?!\w|\.\d)",
        lambda match: match.group("label") + " ".join(
            ONES[int(digit)] for digit in match.group("number")
        ),
        result,
        flags=re.IGNORECASE,
    )
    result = re.sub(
        r"(?<!\w)HP(?!\w)",
        "H P",
        result,
        flags=re.IGNORECASE,
    )
    result = re.sub(
        r"\b(?P<hour>\d{1,2}):(?P<minute>[0-5]\d)"
        r"(?:\s*(?P<suffix>a\.?m\.?|p\.?m\.?))?(?!\w)",
        replace_time,
        result,
        flags=re.IGNORECASE,
    )
    result = re.sub(
        rf"\b(?P<month>{MONTHS})\s+(?P<day>\d{{1,2}})(?:st|nd|rd|th)?"
        rf",?\s+(?P<year>\d{{4}})\b",
        replace_date,
        result,
        flags=re.IGNORECASE,
    )
    result = re.sub(
        r"\b(1\d{3}|20\d{2})\b",
        lambda match: year_to_words(int(match.group(0))),
        result,
    )
    result = re.sub(
        r"\b(\d+)(st|nd|rd|th)\b",
        lambda match: ordinal_to_words(int(match.group(1))),
        result,
        flags=re.IGNORECASE,
    )
    result = re.sub(
        r"(?<![\w.])\d{1,3}(?:,\d{3})*(?!\w|\.\d)",
        lambda match: integer_to_words(
            int(match.group(0).replace(",", ""))
        ),
        result,
    )
    return result


def currency_amounts(text: str) -> list[int]:
    """Extract dollar amounts in cents for exact verification, independent of WER."""
    vocabulary = (*ONES, *TEENS, *TENS[2:], "hundred", "thousand", "million", "and")
    word = "(?:" + "|".join(vocabulary) + ")"
    number = rf"{word}(?:[\s-]+{word})*"
    pattern = rf"\b(?P<dollars>{number})\s+dollars?\b(?:\s+and\s+(?P<cents>{number})\s+cents?\b)?"
    small = {value: index for index, value in enumerate(ONES)}
    small.update({value: 10 + index for index, value in enumerate(TEENS)})
    small.update({value: 10 * index for index, value in enumerate(TENS) if value})

    def value(words: str) -> int:
        total = subtotal = 0
        for token in re.split(r"[\s-]+", words.lower()):
            if token in small:
                subtotal += small[token]
            elif token == "hundred":
                subtotal *= 100
            elif token in ("thousand", "million"):
                total += subtotal * (1000 if token == "thousand" else 1_000_000)
                subtotal = 0
        return total + subtotal

    normalized = normalize_spoken_text(text).lower()
    return [
        value(match.group("dollars")) * 100 + value(match.group("cents") or "zero")
        for match in re.finditer(pattern, normalized)
    ]

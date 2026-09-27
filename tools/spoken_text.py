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

    result = re.sub(
        r"(?<!\w)HP(?!\w)",
        "H P",
        text,
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
        r"(?<![\w.])\d{1,3}(?:,\d{3})*(?![\w.])",
        lambda match: integer_to_words(
            int(match.group(0).replace(",", ""))
        ),
        result,
    )
    return result

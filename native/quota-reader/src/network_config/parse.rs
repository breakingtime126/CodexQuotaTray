// Copyright (c) 2014 Santiago Lapresta and contributors
// SPDX-License-Identifier: MIT
// Adapted from dotenvy 0.15.7, src/parse.rs. See ../../../../third_party/Dotenvy-MIT.txt.
// CHANGED: read-only parser; injectable private environment lookup; bounded expansion;
// closed errors with no source text, keys, paths or values. No dotenv syntax changes.

use std::collections::HashMap;

use super::{Error, Result, MAX_EXPANDED_VALUE_BYTES};

// for readability's sake
pub type ParsedLine = Result<Option<(String, String)>>;

pub fn parse_line(
    line: &str,
    substitution_data: &mut HashMap<String, Option<String>>,
    environment: &dyn Fn(&str) -> Option<String>,
) -> ParsedLine {
    let mut parser = LineParser::new(line, substitution_data, environment);
    parser.parse_line()
}

struct LineParser<'a> {
    substitution_data: &'a mut HashMap<String, Option<String>>,
    environment: &'a dyn Fn(&str) -> Option<String>,
    line: &'a str,
    pos: usize,
}

impl<'a> LineParser<'a> {
    fn new(
        line: &'a str,
        substitution_data: &'a mut HashMap<String, Option<String>>,
        environment: &'a dyn Fn(&str) -> Option<String>,
    ) -> LineParser<'a> {
        LineParser {
            substitution_data,
            environment,
            line: line.trim_end(), // we don’t want trailing whitespace
            pos: 0,
        }
    }

    fn err(&self) -> Error {
        Error::NetworkConfiguration
    }

    fn parse_line(&mut self) -> ParsedLine {
        self.skip_whitespace();
        // if its an empty line or a comment, skip it
        if self.line.is_empty() || self.line.starts_with('#') {
            return Ok(None);
        }

        let mut key = self.parse_key()?;
        self.skip_whitespace();

        // export can be either an optional prefix or a key itself
        if key == "export" {
            // here we check for an optional `=`, below we throw directly when it’s not found.
            if self.expect_equal().is_err() {
                key = self.parse_key()?;
                self.skip_whitespace();
                self.expect_equal()?;
            }
        } else {
            self.expect_equal()?;
        }
        self.skip_whitespace();

        if self.line.is_empty() || self.line.starts_with('#') {
            self.substitution_data.insert(key.clone(), None);
            return Ok(Some((key, String::new())));
        }

        let parsed_value = parse_value(self.line, self.substitution_data, self.environment)?;
        self.substitution_data
            .insert(key.clone(), Some(parsed_value.clone()));

        Ok(Some((key, parsed_value)))
    }

    fn parse_key(&mut self) -> Result<String> {
        if !self
            .line
            .starts_with(|c: char| c.is_ascii_alphabetic() || c == '_')
        {
            return Err(self.err());
        }
        let index = match self
            .line
            .find(|c: char| !(c.is_ascii_alphanumeric() || c == '_' || c == '.'))
        {
            Some(index) => index,
            None => self.line.len(),
        };
        self.pos += index;
        let key = String::from(&self.line[..index]);
        self.line = &self.line[index..];
        Ok(key)
    }

    fn expect_equal(&mut self) -> Result<()> {
        if !self.line.starts_with('=') {
            return Err(self.err());
        }
        self.line = &self.line[1..];
        self.pos += 1;
        Ok(())
    }

    fn skip_whitespace(&mut self) {
        if let Some(index) = self.line.find(|c: char| !c.is_whitespace()) {
            self.pos += index;
            self.line = &self.line[index..];
        } else {
            self.pos += self.line.len();
            self.line = "";
        }
    }
}

#[derive(Eq, PartialEq)]
enum SubstitutionMode {
    None,
    Block,
    EscapedBlock,
}

fn parse_value(
    input: &str,
    substitution_data: &mut HashMap<String, Option<String>>,
    environment: &dyn Fn(&str) -> Option<String>,
) -> Result<String> {
    let mut strong_quote = false; // '
    let mut weak_quote = false; // "
    let mut escaped = false;
    let mut expecting_end = false;

    //FIXME can this be done without yet another allocation per line?
    let mut output = String::new();

    let mut substitution_mode = SubstitutionMode::None;
    let mut substitution_name = String::new();

    for c in input.chars() {
        if output.len() > MAX_EXPANDED_VALUE_BYTES {
            return Err(Error::NetworkConfiguration);
        }
        //the regex _should_ already trim whitespace off the end
        //expecting_end is meant to permit: k=v #comment
        //without affecting: k=v#comment
        //and throwing on: k=v w
        if expecting_end {
            if c == ' ' || c == '\t' {
                continue;
            } else if c == '#' {
                break;
            } else {
                return Err(Error::NetworkConfiguration);
            }
        } else if escaped {
            //TODO I tried handling literal \r but various issues
            //imo not worth worrying about until there's a use case
            //(actually handling backslash 0x10 would be a whole other matter)
            //then there's \v \f bell hex... etc
            match c {
                '\\' | '\'' | '"' | '$' | ' ' => push_char(&mut output, c)?,
                'n' => push_char(&mut output, '\n')?, // handle \n case
                _ => {
                    return Err(Error::NetworkConfiguration);
                }
            }

            escaped = false;
        } else if strong_quote {
            if c == '\'' {
                strong_quote = false;
            } else {
                push_char(&mut output, c)?;
            }
        } else if substitution_mode != SubstitutionMode::None {
            if c.is_alphanumeric() {
                substitution_name.push(c);
            } else {
                match substitution_mode {
                    SubstitutionMode::None => unreachable!(),
                    SubstitutionMode::Block => {
                        if c == '{' && substitution_name.is_empty() {
                            substitution_mode = SubstitutionMode::EscapedBlock;
                        } else {
                            apply_substitution(
                                substitution_data,
                                &substitution_name.drain(..).collect::<String>(),
                                &mut output,
                                environment,
                            )?;
                            if c == '$' {
                                substitution_mode = if !strong_quote && !escaped {
                                    SubstitutionMode::Block
                                } else {
                                    SubstitutionMode::None
                                }
                            } else {
                                substitution_mode = SubstitutionMode::None;
                                push_char(&mut output, c)?;
                            }
                        }
                    }
                    SubstitutionMode::EscapedBlock => {
                        if c == '}' {
                            substitution_mode = SubstitutionMode::None;
                            apply_substitution(
                                substitution_data,
                                &substitution_name.drain(..).collect::<String>(),
                                &mut output,
                                environment,
                            )?;
                        } else {
                            substitution_name.push(c);
                        }
                    }
                }
            }
        } else if c == '$' {
            substitution_mode = if !strong_quote && !escaped {
                SubstitutionMode::Block
            } else {
                SubstitutionMode::None
            }
        } else if weak_quote {
            if c == '"' {
                weak_quote = false;
            } else if c == '\\' {
                escaped = true;
            } else {
                push_char(&mut output, c)?;
            }
        } else if c == '\'' {
            strong_quote = true;
        } else if c == '"' {
            weak_quote = true;
        } else if c == '\\' {
            escaped = true;
        } else if c == ' ' || c == '\t' {
            expecting_end = true;
        } else {
            push_char(&mut output, c)?;
        }
    }

    //XXX also fail if escaped? or...
    if substitution_mode == SubstitutionMode::EscapedBlock || strong_quote || weak_quote {
        Err(Error::NetworkConfiguration)
    } else {
        apply_substitution(
            substitution_data,
            &substitution_name.drain(..).collect::<String>(),
            &mut output,
            environment,
        )?;
        Ok(output)
    }
}

fn apply_substitution(
    substitution_data: &mut HashMap<String, Option<String>>,
    substitution_name: &str,
    output: &mut String,
    environment: &dyn Fn(&str) -> Option<String>,
) -> Result<()> {
    // The official parser consults the process environment first, then its
    // case-sensitive parsed-symbol table. The caller supplies an isolated overlay
    // that models official sequential set_var calls without exporting those keys.
    let environment_value = environment(substitution_name);
    let value = environment_value
        .as_deref()
        .or_else(|| {
            substitution_data
                .get(substitution_name)
                .and_then(Option::as_deref)
        })
        .unwrap_or_default();
    if value.len() > MAX_EXPANDED_VALUE_BYTES.saturating_sub(output.len()) {
        return Err(Error::NetworkConfiguration);
    }
    output.push_str(value);
    Ok(())
}

fn push_char(output: &mut String, character: char) -> Result<()> {
    if character.len_utf8() > MAX_EXPANDED_VALUE_BYTES.saturating_sub(output.len()) {
        return Err(Error::NetworkConfiguration);
    }
    output.push(character);
    Ok(())
}

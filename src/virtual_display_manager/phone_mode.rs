// Bound capture cost on mobile links; hardware encoders need even dimensions.
pub(super) fn fit(width: i32, height: i32) -> Option<(u32, u32)> {
    if !(240..=16384).contains(&width) || !(240..=16384).contains(&height) {
        return None;
    }
    let longest = width.max(height);
    let shortest = width.min(height);
    if longest > shortest * 4 {
        return None;
    }
    let scale = (1920.0 / f64::from(longest))
        .min(1080.0 / f64::from(shortest))
        .min(1.0);
    let even = |n| ((f64::from(n) * scale / 2.0).round() as u32) * 2;
    Some((even(width), even(height)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounds_both_orientations_without_stretching() {
        assert_eq!(fit(1080, 2400), Some((864, 1920)));
        assert_eq!(fit(2400, 1080), Some((1920, 864)));
        assert_eq!(fit(720, 1280), Some((720, 1280)));
        assert_eq!(fit(0, i32::MAX), None);
    }
}
